using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
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

    private Manifest? manifest;

    public String CurrentChannel { get; private set; }

    public ManifestReleasesFeed(IFileSystem fileSystem, IManifestReleasesFeedProviderService manifestReleasesFeedProviderService,
        ILogService log, string downloadStorageDir, FeedInfo feed)
    {
        _fileSystem = fileSystem;
        _manifestReleasesFeedProviderService = manifestReleasesFeedProviderService;
        _log = log;
        _downloadStorageDir = downloadStorageDir;
        _feed = feed;
    }

    public class Patch
    {
        public string version { get; set; }
        public Requires requires { get; set; }
        public string url { get; set; }
        public string checksum_sha256 { get; set; }
        public int size { get; set; }
        public DateTime releasedAt { get; set; }

        public GameVersion ParseVersion()
        {
            var tokens = version.Split(new[] { '.', '-' });
            // remove optional leading 'v' from version
            if (tokens[0][0] == 'v')
            {
                tokens[0] = tokens[0][1..];
            }

            Version versionNumber;
            string CommitHash;
            if (tokens.Length > 4)
            {
                versionNumber = new Version(string.Join('.', tokens[0..4]));
                CommitHash = tokens[4];
            }
            else
            {
                versionNumber = new Version(string.Join('.', tokens));
                CommitHash = "0";
            }

            return new GameVersion()
            {
                VersionNumber = versionNumber,
                BuildNumber = CommitHash,
                CommitHash = CommitHash
            };
        }
    }

    public class Requires
    {
        public string? version { get; set; }

        [JsonIgnore] public bool IsBasePatch => string.IsNullOrEmpty(version);
    }

    public class Manifest
    {
        public int schemaVersion { get; set; }
        public string channel { get; set; }
        public DateTime generatedAt { get; set; }
        public List<Patch> patches { get; set; }
    }


    public async Task UpdateManifest()
    {
        _log.Info($"Updating manifest for feed {_feed.Repository} / {_feed.Filename}.");
        try
        {
            manifest = await _manifestReleasesFeedProviderService.GetManifest(_feed);
            if (manifest is null)
            {
                _log.Warn($"Manifest for {_feed.Repository} / {_feed.Filename} was null. Marking channel as invalid.");
                manifest = new Manifest
                {
                    schemaVersion = 0,
                    patches = [],
                    channel = "invalid",
                    generatedAt = DateTime.MinValue,
                };
                CurrentChannel = "invalid";
                return;
            }
            CurrentChannel = manifest.channel;
            _log.Info($"Manifest loaded for {_feed.Repository} / {_feed.Filename}. Channel={CurrentChannel}, Patches={manifest.patches?.Count ?? 0}, GeneratedAt={manifest.generatedAt:O}.");
        }
        catch (Exception e)
        {
            _log.Error($"Could not download or parse manifest for {_feed.Repository} / {_feed.Filename}. Marking channel as invalid.", e);
            manifest = new Manifest
            {
                schemaVersion = 0,
                patches = [],
                channel = "invalid",
                generatedAt = DateTime.MinValue,
            };
            CurrentChannel = "invalid";
        }
    }

    public IEnumerable<GameVersion> GetAllVersions()
    {
        if (manifest?.patches != null)
            foreach (var release in manifest.patches)
            {
                var pversion = release.ParseVersion();
                pversion.Channel = CurrentChannel;
                pversion.ReleasedAt = release.releasedAt;
                yield return pversion;
            }
    }

    public GameVersion? GetLatestVersion()
    {
        var latest = manifest?.patches?
            .Where(p => !string.IsNullOrWhiteSpace(p.version))
            .OrderByDescending(p => p.releasedAt)
            .FirstOrDefault();
        if (latest is null) return null;
        var v = latest.ParseVersion();
        v.Channel = CurrentChannel;
        v.ReleasedAt = latest.releasedAt;
        return v;
    }

    // private InstallPlan GetBestFromPrepatchToVersion(GameVersion toGameVersion)
    // {
    //     var lowestCostSoFar = int.MaxValue;
    //     InstallPlan bestPlan = new InstallPlan();
    //     foreach (var release in manifest.patches)
    //     {
    //         if (string.Equals())
    //     }
    // }
    
    //if user selects the initial version as to install then skip later patch checking as only 1 patch needed
    // public InstallPlan GetPatchListToVersion(GameVersion toGameVersion)
    // {
    //     
    //     var plan = new InstallPlan();
    //     if (manifest?.patches is null)
    //         return plan;
    //
    //     
    //     // Patch? matchingPrepatch = null;
    //     // foreach (var release in manifest.patches)
    //     // {
    //     //     if (!string.IsNullOrWhiteSpace(release.requires?.distribution) &&
    //     //         string.Equals(release.requires.distribution, distribution, StringComparison.OrdinalIgnoreCase))
    //     //     {
    //     //         matchingPrepatch = release;
    //     //         break;
    //     //     }
    //     // }
    //
    //     var prepatchVersion = matchingPrepatch.ParseVersion();
    //
    //     bool numericBuild = !string.IsNullOrEmpty(toGameVersion.CommitHash) &&
    //                         toGameVersion.CommitHash.All(char.IsDigit);
    //     string sep = numericBuild ? "." : "-";
    //     string targetVersion = $"{toGameVersion.VersionNumber}{sep}{toGameVersion.CommitHash}";
    //     if (string.Equals(matchingPrepatch.version, targetVersion, StringComparison.OrdinalIgnoreCase))
    //     {
    //         return new List<Patch> { matchingPrepatch };
    //     }
    //
    //     var patchList = GetPatchListToVersion(prepatchVersion, toGameVersion);
    //     patchList.Reverse();
    //     patchList.Add(matchingPrepatch);
    //     patchList.Reverse();
    //     return patchList;
    // }
    //find the best path to use to get user to the correct game version from their game version
    
    
    public InstallPlan GetPatchListToVersion(GameVersion fromGameVersion, GameVersion toGameVersion)
    {
        if (manifest?.patches is null) return new InstallPlan();

        static string ToVersionString(GameVersion gv) => $"{gv.VersionNumber}.{gv.BuildNumber}";

        string startVersion = ToVersionString(fromGameVersion);
        string targetVersion = ToVersionString(toGameVersion);

        var patchesByOutput = manifest.patches
            .Where(p => !string.IsNullOrWhiteSpace(p.version))
            .GroupBy(p => p.version, StringComparer.OrdinalIgnoreCase)
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
                    if (patch.requires.version == from)
                    {
                        bestPlan = new InstallPlan();
                        bestPlan.ApplyPatchFile((log, progress,ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from version {from}");
                        break;
                    }
                    
                    if (patch.requires.IsBasePatch)
                    {
                        var testPlan = new InstallPlan();
                        testPlan.ApplyPatchFile((log, progress, ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from prepatch");
                        testPlan.Prepatch();
                        testPlan.RevertToStock();
                        if (bestPlan == null || bestPlan.Cost > testPlan.Cost) bestPlan = testPlan;
                    }
                    else
                    {
                        var newInitialPlan = new InstallPlan();
                        newInitialPlan.ApplyPatchFile((log, progress, ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from version {patch.requires.version}");
                        var testPlan = GetPlan(from, patch.requires.version!, newInitialPlan);
                        if (testPlan != null && (bestPlan == null || bestPlan.Cost > testPlan.Cost)) bestPlan = testPlan;
                    }
                }
            }

            if (bestPlan == null) return null;
            return bestPlan + initialPlan;
        }
    }

    public async Task<string> DownloadPatch(Patch patch, Action<string> log, Action<long, long> reportDownloadProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string FileName = patch.url.Split("/").Last();
        string patchDownloadTo = _fileSystem.Path.Combine(_downloadStorageDir, FileName);

        log($"Downloading {FileName}");
        reportDownloadProgress(0, patch.size);

        if (!_fileSystem.File.Exists(patchDownloadTo) || _fileSystem.FileInfo.New(patchDownloadTo).Length != patch.size)
        {
            using var downloadResponse = await _manifestReleasesFeedProviderService.DownloadPatchAsync(_feed, patch, ct);
            long contentLength = downloadResponse.Content.Headers.ContentLength ?? patch.size;

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
            reportDownloadProgress(patch.size, patch.size);
        }

        log("Download complete.");
        return patchDownloadTo;
    }
}