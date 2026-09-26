using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Net;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using SteamKit2;
using SteamKit2.CDN;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

/// <summary>
/// One Steam depot to download, as listed in the app's product info.
/// </summary>
public sealed record SteamDepot(uint DepotId, ulong ManifestId, ulong Size);

/// <summary>
/// Progress through a game download.
/// </summary>
public sealed record SteamDownloadProgress(long DownloadedBytes, long TotalBytes, int FilesDone, int FilesTotal, string Stage);

public interface ISteamDepotDownloader
{
    /// <summary>
    /// Lists the Windows depots that make up the game, checking the signed-in account owns it.
    /// </summary>
    /// <exception cref="SteamSignInException">Not signed in, or the account does not own the game.</exception>
    Task<IReadOnlyList<SteamDepot>> GetDepotsAsync(uint appId, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the Windows build of the game into <paramref name="targetDirectory"/>. Files already
    /// there and matching Steam's checksums are kept, so an interrupted download picks up where it stopped.
    /// </summary>
    Task DownloadAsync(uint appId, string targetDirectory, IProgress<SteamDownloadProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads a game's Windows depots straight from Steam's content servers.
/// </summary>
public class SteamDepotDownloader(ISteamSessionService session, IFileSystem fileSystem, ILogService log) : ISteamDepotDownloader
{
    public const uint KSP2_APP_ID = 954850;
    public const string GAME_FOLDER_NAME = "Kerbal Space Program 2";

    /// <summary>
    /// Works out the folder a download into <paramref name="chosen"/> goes to.
    /// </summary>
    /// <returns>
    /// <paramref name="chosen"/> itself when it is already a "Kerbal Space Program 2" folder, otherwise a
    /// "Kerbal Space Program 2" folder inside it.
    /// </returns>
    public static string GameFolderIn(IFileSystem fileSystem, string chosen)
    {
        var trimmed = chosen.TrimEnd(fileSystem.Path.DirectorySeparatorChar, fileSystem.Path.AltDirectorySeparatorChar);
        return string.Equals(fileSystem.Path.GetFileName(trimmed), GAME_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : fileSystem.Path.Combine(trimmed, GAME_FOLDER_NAME);
    }
    private const string BRANCH = "public";
    private const int MAX_CONCURRENT_CHUNKS = 8;
    private const int MAX_CHUNK_ATTEMPTS = 5;

    public async Task<IReadOnlyList<SteamDepot>> GetDepotsAsync(uint appId, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        var tokens = await connection.Apps.PICSGetAccessTokens(appId, null);
        var request = new SteamApps.PICSRequest(appId);
        if (tokens.AppTokens.TryGetValue(appId, out var token)) request.AccessToken = token;

        var productInfo = await connection.Apps.PICSGetProductInfo(request, null);
        var app = productInfo.Results?
            .SelectMany(result => result.Apps)
            .Where(pair => pair.Key == appId)
            .Select(pair => pair.Value)
            .FirstOrDefault()
            ?? throw new SteamSignInException("Steam didn't return any information about Kerbal Space Program 2.");

        var depots = SelectWindowsDepots(app.KeyValues["depots"], BRANCH);
        if (depots.Count == 0)
        {
            throw new SteamSignInException("Steam didn't list any downloadable Windows content for Kerbal Space Program 2.");
        }

        // Steam only issues depot keys to owners, so this doubles as the ownership check.
        foreach (var depot in depots)
        {
            var key = await connection.Apps.GetDepotDecryptionKey(depot.DepotId, appId);
            if (key.Result != EResult.OK)
            {
                throw new SteamSignInException(
                    "This Steam account doesn't own Kerbal Space Program 2. Sign in with the account you bought it on.",
                    key.Result);
            }
        }

        return depots;
    }

    public async Task DownloadAsync(uint appId, string targetDirectory, IProgress<SteamDownloadProgress> progress, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        progress.Report(new SteamDownloadProgress(0, 0, 0, 0, "Checking your Steam library"));
        var depots = await GetDepotsAsync(appId, cancellationToken);

        var servers = await GetContentServersAsync(connection, appId);
        using var cdn = new Client(connection.Client);
        var authTokens = new ConcurrentDictionary<(uint Depot, string Host), string?>();

        var work = new List<(SteamDepot Depot, byte[] Key, DepotManifest Manifest)>();
        foreach (var depot in depots)
        {
            progress.Report(new SteamDownloadProgress(0, 0, 0, 0, "Reading the file list"));
            var key = (await connection.Apps.GetDepotDecryptionKey(depot.DepotId, appId)).DepotKey;
            var requestCode = await connection.Content.GetManifestRequestCode(depot.DepotId, appId, depot.ManifestId, BRANCH);
            var manifest = await WithServerRetry(servers, depot, appId, connection, authTokens, cancellationToken,
                (server, token) => cdn.DownloadManifestAsync(depot.DepotId, depot.ManifestId, requestCode, server, key, null, token));
            if (manifest.FilenamesEncrypted) manifest.DecryptFilenames(key);
            work.Add((depot, key, manifest));
        }

        var files = work
            .SelectMany(item => (item.Manifest.Files ?? []).Select(file => (item.Depot, item.Key, File: file)))
            .ToList();
        long totalBytes = files.Where(f => !IsDirectory(f.File)).Sum(f => (long)f.File.TotalSize);
        long downloadedBytes = 0;
        var filesDone = 0;
        var filesTotal = files.Count(f => !IsDirectory(f.File));

        void Report(string stage) => progress.Report(new SteamDownloadProgress(
            Interlocked.Read(ref downloadedBytes), totalBytes, Volatile.Read(ref filesDone), filesTotal, stage));

        fileSystem.Directory.CreateDirectory(targetDirectory);
        foreach (var (_, _, file) in files.Where(f => IsDirectory(f.File)))
        {
            fileSystem.Directory.CreateDirectory(ResolveInside(targetDirectory, file.FileName));
        }

        using var throttle = new SemaphoreSlim(MAX_CONCURRENT_CHUNKS);
        Report("Downloading");

        await Parallel.ForEachAsync(files.Where(f => !IsDirectory(f.File)),
            new ParallelOptions { MaxDegreeOfParallelism = MAX_CONCURRENT_CHUNKS, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                var path = ResolveInside(targetDirectory, item.File.FileName);
                fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(path)!);

                if (await IsAlreadyComplete(path, item.File, token))
                {
                    Interlocked.Add(ref downloadedBytes, (long)item.File.TotalSize);
                    Interlocked.Increment(ref filesDone);
                    Report("Downloading");
                    return;
                }

                await using (var stream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength((long)item.File.TotalSize);
                    var writeLock = new SemaphoreSlim(1, 1);
                    await Task.WhenAll(item.File.Chunks.Select(async chunk =>
                    {
                        await throttle.WaitAsync(token);
                        var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
                        try
                        {
                            var written = await WithServerRetry(servers, item.Depot, appId, connection, authTokens, token,
                                (server, cdnToken) => cdn.DownloadDepotChunkAsync(item.Depot.DepotId, chunk, server, buffer, item.Key, null, cdnToken));

                            await writeLock.WaitAsync(token);
                            try
                            {
                                stream.Position = (long)chunk.Offset;
                                await stream.WriteAsync(buffer.AsMemory(0, written), token);
                            }
                            finally
                            {
                                writeLock.Release();
                            }

                            Interlocked.Add(ref downloadedBytes, written);
                            Report("Downloading");
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            throttle.Release();
                        }
                    }));
                }

                Interlocked.Increment(ref filesDone);
                Report("Downloading");
            });

        Report("Finished");
        log.Info($"Downloaded app {appId} ({filesTotal} files, {totalBytes} bytes) into {targetDirectory}.");
    }

    /// <summary>
    /// Picks the depots a Windows install needs from an app's <c>depots</c> product-info section.
    /// </summary>
    internal static IReadOnlyList<SteamDepot> SelectWindowsDepots(KeyValue depotsSection, string branch)
    {
        var depots = new List<SteamDepot>();
        foreach (var depot in depotsSection.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId)) continue;
            if (depot["depotfromapp"] != KeyValue.Invalid || depot["dlcappid"] != KeyValue.Invalid) continue;

            var osList = depot["config"]["oslist"].Value;
            if (!string.IsNullOrWhiteSpace(osList) &&
                !osList.Split(',').Contains("windows", StringComparer.OrdinalIgnoreCase)) continue;

            var branchNode = depot["manifests"][branch];
            // Newer product info nests the manifest id under "gid", older entries store it directly.
            var gid = branchNode["gid"] != KeyValue.Invalid ? branchNode["gid"].Value : branchNode.Value;
            if (!ulong.TryParse(gid, out var manifestId)) continue;

            var sizeText = branchNode["size"] != KeyValue.Invalid ? branchNode["size"].Value : depot["maxsize"].Value;
            ulong.TryParse(sizeText, out var size);
            depots.Add(new SteamDepot(depotId, manifestId, size));
        }

        return depots;
    }

    /// <summary>
    /// Joins a depot file name onto the install folder, refusing names that would escape it.
    /// </summary>
    internal string ResolveInside(string root, string depotFileName)
    {
        var relative = depotFileName.Replace('\\', fileSystem.Path.DirectorySeparatorChar);
        var fullRoot = fileSystem.Path.GetFullPath(root);
        var full = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(fullRoot, relative));
        var rootWithSeparator = fullRoot.EndsWith(fileSystem.Path.DirectorySeparatorChar) ? fullRoot : fullRoot + fileSystem.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && full != fullRoot)
        {
            throw new InvalidDataException($"The depot contains a file outside the install folder: {depotFileName}");
        }

        return full;
    }

    private static bool IsDirectory(DepotManifest.FileData file) => file.Flags.HasFlag(EDepotFileFlag.Directory);

    private async Task<bool> IsAlreadyComplete(string path, DepotManifest.FileData file, CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(path) || fileSystem.FileInfo.New(path).Length != (long)file.TotalSize) return false;

        await using var stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return hash.AsSpan().SequenceEqual(file.FileHash);
    }

    private SteamConnection RequireConnection() =>
        session.Connection ?? throw new SteamSignInException("Sign in with Steam first.");

    private async Task<IReadOnlyList<Server>> GetContentServersAsync(SteamConnection connection, uint appId)
    {
        var servers = (await connection.Content.GetServersForSteamPipe())
            .Where(server => server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(appId))
            .Where(server => server.Type is "SteamCache" or "CDN")
            .OrderBy(server => server.WeightedLoad)
            .ToList();

        return servers.Count > 0
            ? servers
            : throw new SteamSignInException("Steam didn't return any content servers. Try again in a moment.");
    }

    // A 403 means the server wants a CDN auth token for this depot.
    private async Task<T> WithServerRetry<T>(
        IReadOnlyList<Server> servers, SteamDepot depot, uint appId, SteamConnection connection,
        ConcurrentDictionary<(uint Depot, string Host), string?> authTokens, CancellationToken cancellationToken,
        Func<Server, string?, Task<T>> action)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < MAX_CHUNK_ATTEMPTS; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = servers[(Random.Shared.Next(servers.Count) + attempt) % servers.Count];
            authTokens.TryGetValue((depot.DepotId, server.Host!), out var cdnToken);

            try
            {
                return await action(server, cdnToken);
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden && cdnToken is null)
            {
                var auth = await connection.Content.GetCDNAuthToken(appId, depot.DepotId, server.Host!);
                authTokens[(depot.DepotId, server.Host!)] = auth.Result == EResult.OK ? auth.Token : null;
                last = ex;
            }
            catch (Exception ex) when ((ex is SteamKitWebRequestException or HttpRequestException or IOException or TaskCanceledException)
                                           && !cancellationToken.IsCancellationRequested)
            {
                log.Debug($"Content server {server.Host} failed ({ex.Message}), trying another.");
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
            }
        }

        throw new IOException("Steam's content servers kept failing. Check your connection and try again.", last);
    }
}
