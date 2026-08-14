using System.Diagnostics;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Models;

public class ManifestReleasesFeed(
    IFileSystem fileSystem,
    IManifestReleasesFeedProviderService manifestReleasesFeedProviderService,
    ILogService log,
    string downloadStorageDir,
    FeedInfo feed)
{
    private ReleaseManifest? _manifest;

    public Exception? LastUpdateException { get; private set; }

    public string CurrentChannel { get; private set; } = "invalid";

    /// <returns>false if the fetch failed, true otherwise. On failure, a previously-loaded manifest
    /// (and CurrentChannel) is left untouched rather than replaced, so callers showing a "using the
    /// last known list" message after a failed refresh are telling the truth. Only falls back to an
    /// empty "invalid" placeholder if there was never a successful fetch to fall back to.</returns>
    public async Task<bool> UpdateManifest()
    {
        log.Info($"Updating manifest for feed {feed.Repository} / {feed.Filename}.");
        try
        {
            LastUpdateException = null;
            var manifest = await manifestReleasesFeedProviderService.GetManifest(feed);
            if (manifest is null)
            {
                log.Warn($"Manifest for {feed.Repository} / {feed.Filename} was null. Keeping the last known list, if any.");
                FallBackToInvalidIfNeverLoaded();
                return false;
            }
            _manifest = manifest;
            CurrentChannel = _manifest.Channel;
            log.Info($"Manifest loaded for {feed.Repository} / {feed.Filename}. Channel={CurrentChannel}, Patches={_manifest.Patches?.Count ?? 0}, GeneratedAt={_manifest.GeneratedAt:O}.");
            return true;
        }
        catch (Exception e)
        {
            LastUpdateException = e;
            log.Error($"Could not download or parse manifest for {feed.Repository} / {feed.Filename}. Keeping the last known list, if any.", e);
            FallBackToInvalidIfNeverLoaded();
            return false;
        }
    }

    /// <summary>
    /// Downloads a legacy single-URL patch. New install plans use the shared chunk download service.
    /// </summary>
    public async Task<string> DownloadPatch(
        ReleasePatch patch, Action<string> downloadLog, Action<long, long> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch.Url))
            throw new InvalidOperationException("The patch does not have a legacy download URL.");
        string fileName = patch.Url.Split('/').Last();
        string destination = fileSystem.Path.Combine(downloadStorageDir, fileName);
        progress(0, patch.Size);
        if (!await MatchesChecksum(destination, patch, ct))
        {
            using var response = await manifestReleasesFeedProviderService.DownloadPatchAsync(feed, patch, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            {
                await using var output = fileSystem.FileStream.New(
                    destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
                var buffer = new byte[64 * 1024];
                long written = 0;
                var timer = Stopwatch.StartNew();
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (timer.ElapsedMilliseconds >= 100)
                    {
                        progress(written, patch.Size);
                        timer.Restart();
                    }
                }
                await output.FlushAsync(ct);
            }
            if (!await MatchesChecksum(destination, patch, ct))
            {
                fileSystem.File.Delete(destination);
                throw new InvalidOperationException("The downloaded patch did not match its expected checksum.");
            }
        }
        progress(patch.Size, patch.Size);
        downloadLog("Download complete.");
        return destination;
    }

    private async Task<bool> MatchesChecksum(string path, ReleasePatch patch, CancellationToken ct)
    {
        if (!fileSystem.File.Exists(path) || fileSystem.FileInfo.New(path).Length != patch.Size)
            return false;
        await using var stream = fileSystem.File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), patch.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void FallBackToInvalidIfNeverLoaded()
    {
        if (_manifest is not null) return;
        _manifest = new ReleaseManifest
        {
            SchemaVersion = 0,
            Patches = [],
            Channel = "invalid",
            GeneratedAt = DateTime.MinValue
        };
        CurrentChannel = "invalid";
    }

    public IEnumerable<GameVersion> GetAllVersions()
    {
        if (_manifest?.Patches != null)
            foreach (var release in _manifest.Patches)
            {
                var pversion = release.ParseVersion();
                pversion.Channel = CurrentChannel;
                pversion.ReleasedAt = release.ReleasedAt;
                pversion.Label = release.Label;
                yield return pversion;
            }
    }

    public GameVersion? GetLatestVersion()
    {
        var latest = _manifest?.Patches?
            .Where(p => !string.IsNullOrWhiteSpace(p.Version))
            .OrderByDescending(p => p.ReleasedAt)
            .FirstOrDefault();
        if (latest is null) return null;
        var v = latest.ParseVersion();
        v.Channel = CurrentChannel;
        v.ReleasedAt = latest.ReleasedAt;
        v.Label = latest.Label;
        return v;
    }

    //find the best path to use to get user to the correct game version from their game version
    public InstallPlan GetPatchListToVersion(GameVersion fromGameVersion, GameVersion toGameVersion)
    {
        if (_manifest?.Patches is null) return new InstallPlan();

        static string ToVersionString(GameVersion gv) => $"{gv.VersionNumber}.{gv.BuildNumber}";

        string startVersion = ToVersionString(fromGameVersion);
        string targetVersion = ToVersionString(toGameVersion);

        var patchesByOutput = _manifest.Patches
            .Where(p => !string.IsNullOrWhiteSpace(p.Version))
            .GroupBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (!patchesByOutput.ContainsKey(targetVersion))
            return new InstallPlan();

        if (GetPlan(startVersion, targetVersion, new InstallPlan()) is {} result)
        {
            return result;
        }

        return new InstallPlan();

        InstallPlan? GetPlan(string from, string to, InstallPlan initialPlan)
        {
            InstallPlan? bestPlan = null;

            if (patchesByOutput.TryGetValue(to, out var patches))
            {
                foreach (var patch in patches)
                {
                    if (patch.Requires.Version == from)
                    {
                        bestPlan = new InstallPlan();
                        bestPlan.ApplyPatchFile(new PatchDownloadRequest(feed, patch, downloadStorageDir), $"applying patch for version: {to} from version {from}");
                        break;
                    }

                    if (patch.Requires.IsBasePatch)
                    {
                        var testPlan = new InstallPlan();
                        testPlan.ApplyPatchFile(new PatchDownloadRequest(feed, patch, downloadStorageDir), $"applying patch for version: {to} from prepatch");
                        testPlan.Prepatch();
                        testPlan.RevertToStock();
                        if (bestPlan == null || bestPlan.Cost > testPlan.Cost) bestPlan = testPlan;
                    }
                    else
                    {
                        var newInitialPlan = new InstallPlan();
                        newInitialPlan.ApplyPatchFile(new PatchDownloadRequest(feed, patch, downloadStorageDir), $"applying patch for version: {to} from version {patch.Requires.Version}");
                        var testPlan = GetPlan(from, patch.Requires.Version!, newInitialPlan);
                        if (testPlan != null && (bestPlan == null || bestPlan.Cost > testPlan.Cost)) bestPlan = testPlan;
                    }
                }
            }

            if (bestPlan == null) return null;
            return bestPlan + initialPlan;
        }
    }

}
