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
            log.Error($"Could not download or parse manifest for {feed.Repository} / {feed.Filename}. Keeping the last known list, if any.", e);
            FallBackToInvalidIfNeverLoaded();
            return false;
        }
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
                        bestPlan.ApplyPatchFile((log, progress,ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from version {from}", patch.Size);
                        break;
                    }

                    if (patch.Requires.IsBasePatch)
                    {
                        var testPlan = new InstallPlan();
                        testPlan.ApplyPatchFile((log, progress, ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from prepatch", patch.Size);
                        testPlan.Prepatch();
                        testPlan.RevertToStock();
                        if (bestPlan == null || bestPlan.Cost > testPlan.Cost) bestPlan = testPlan;
                    }
                    else
                    {
                        var newInitialPlan = new InstallPlan();
                        newInitialPlan.ApplyPatchFile((log, progress, ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from version {patch.Requires.Version}", patch.Size);
                        var testPlan = GetPlan(from, patch.Requires.Version!, newInitialPlan);
                        if (testPlan != null && (bestPlan == null || bestPlan.Cost > testPlan.Cost)) bestPlan = testPlan;
                    }
                }
            }

            if (bestPlan == null) return null;
            return bestPlan + initialPlan;
        }
    }

    public async Task<string> DownloadPatch(ReleasePatch patch, Action<string> log1, Action<long, long> reportDownloadProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fileName = patch.Url.Split("/").Last();
        string patchDownloadTo = fileSystem.Path.Combine(downloadStorageDir, fileName);

        log1($"Downloading {fileName}");
        reportDownloadProgress(0, patch.Size);

        bool cacheIsUsable = fileSystem.File.Exists(patchDownloadTo) &&
                              fileSystem.FileInfo.New(patchDownloadTo).Length == patch.Size &&
                              await MatchesChecksum(patchDownloadTo, patch, ct);

        if (!cacheIsUsable)
        {
            using var downloadResponse = await manifestReleasesFeedProviderService.DownloadPatchAsync(feed, patch, ct);
            long contentLength = downloadResponse.Content.Headers.ContentLength ?? patch.Size;

            await using (var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = fileSystem.FileStream.New(patchDownloadTo, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long totalBytesRead = 0;
                int bytesRead;
                var updateTimer = Stopwatch.StartNew();

                while ((bytesRead = await downloadStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalBytesRead += bytesRead;

                    if (updateTimer.ElapsedMilliseconds > 100)
                    {
                        reportDownloadProgress(totalBytesRead, contentLength);
                        updateTimer.Restart();
                    }
                }
            }

            if (!await MatchesChecksum(patchDownloadTo, patch, ct))
            {
                log.Error($"Downloaded {fileName} failed checksum verification. Deleting corrupt download.");
                fileSystem.File.Delete(patchDownloadTo);
                throw new InvalidOperationException(
                    $"Downloaded patch {fileName} did not match its expected checksum. The download may be corrupt or incomplete - please try again.");
            }
        }
        else
        {
            reportDownloadProgress(patch.Size, patch.Size);
        }

        log1("Download complete.");
        return patchDownloadTo;
    }

    private async Task<bool> MatchesChecksum(string filePath, ReleasePatch patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch.ChecksumSha256))
        {
            log.Error($"No checksum available for patch at {patch.Url}, refusing to trust it.");
            return false;
        }

        await using var stream = fileSystem.File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var actual = Convert.ToHexString(hash);
        return string.Equals(actual, patch.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
    }
}
