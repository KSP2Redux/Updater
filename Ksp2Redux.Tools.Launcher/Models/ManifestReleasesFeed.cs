using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Launcher.Models;

public class ManifestReleasesFeed
{
    private readonly IFileSystem _fileSystem;
    private readonly IManifestReleasesFeedProviderService _manifestReleasesFeedProviderService;
    private readonly ILogService _log;

    private readonly string _downloadStorageDir;
    private readonly FeedInfo _feed;

    private ReleaseManifest? _manifest;

    public string CurrentChannel { get; private set; }

    public ManifestReleasesFeed(IFileSystem fileSystem, IManifestReleasesFeedProviderService manifestReleasesFeedProviderService,
        ILogService log, string downloadStorageDir, FeedInfo feed)
    {
        _fileSystem = fileSystem;
        _manifestReleasesFeedProviderService = manifestReleasesFeedProviderService;
        _log = log;
        _downloadStorageDir = downloadStorageDir;
        _feed = feed;
    }

    public async Task UpdateManifest()
    {
        _log.Info($"Updating manifest for feed {_feed.Repository} / {_feed.Filename}.");
        try
        {
            _manifest = await _manifestReleasesFeedProviderService.GetManifest(_feed);
            if (_manifest is null)
            {
                _log.Warn($"Manifest for {_feed.Repository} / {_feed.Filename} was null. Marking channel as invalid.");
                _manifest = new ReleaseManifest
                {
                    SchemaVersion = 0,
                    Patches = [],
                    Channel = "invalid",
                    GeneratedAt = DateTime.MinValue,
                };
                CurrentChannel = "invalid";
                return;
            }
            CurrentChannel = _manifest.Channel;
            _log.Info($"Manifest loaded for {_feed.Repository} / {_feed.Filename}. Channel={CurrentChannel}, Patches={_manifest.Patches?.Count ?? 0}, GeneratedAt={_manifest.GeneratedAt:O}.");
        }
        catch (Exception e)
        {
            _log.Error($"Could not download or parse manifest for {_feed.Repository} / {_feed.Filename}. Marking channel as invalid.", e);
            _manifest = new ReleaseManifest
            {
                SchemaVersion = 0,
                Patches = [],
                Channel = "invalid",
                GeneratedAt = DateTime.MinValue,
            };
            CurrentChannel = "invalid";
        }
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

    public async Task<string> DownloadPatch(ReleasePatch patch, Action<string> log, Action<long, long> reportDownloadProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fileName = patch.Url.Split("/").Last();
        string patchDownloadTo = _fileSystem.Path.Combine(_downloadStorageDir, fileName);

        log($"Downloading {fileName}");
        reportDownloadProgress(0, patch.Size);

        if (!_fileSystem.File.Exists(patchDownloadTo) || _fileSystem.FileInfo.New(patchDownloadTo).Length != patch.Size)
        {
            using var downloadResponse = await _manifestReleasesFeedProviderService.DownloadPatchAsync(_feed, patch, ct);
            long contentLength = downloadResponse.Content.Headers.ContentLength ?? patch.Size;

            using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(ct);
            using var fileStream = _fileSystem.FileStream.New(patchDownloadTo, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

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
        else
        {
            reportDownloadProgress(patch.Size, patch.Size);
        }

        log("Download complete.");
        return patchDownloadTo;
    }
}
