using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Models;

public class ManifestReleasesFeed(
    IManifestReleasesFeedProviderService manifestReleasesFeedProviderService,
    ILogService log,
    string downloadStorageDir,
    FeedInfo feed)
{
    private ReleaseManifest? _manifest;

    public Exception? LastUpdateException { get; private set; }

    public const string INVALID_CHANNEL = "invalid";

    public string CurrentChannel { get; private set; } = INVALID_CHANNEL;

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

    private void FallBackToInvalidIfNeverLoaded()
    {
        if (_manifest is not null) return;
        _manifest = new ReleaseManifest
        {
            SchemaVersion = 0,
            Patches = [],
            Channel = INVALID_CHANNEL,
            GeneratedAt = DateTime.MinValue
        };
        CurrentChannel = INVALID_CHANNEL;
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

        if (GetFullSourcePlan(toGameVersion) is { } fullSource)
            return fullSource;

        var patchesByOutput = _manifest.Patches
            .Where(p => !string.IsNullOrWhiteSpace(p.Version) && p.FullSource is null)
            .GroupBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (!patchesByOutput.ContainsKey(targetVersion))
            return new InstallPlan();

        HashSet<string> visiting = [with(StringComparer.OrdinalIgnoreCase)];
        if (GetPlan(startVersion, targetVersion, new InstallPlan()) is {} result)
        {
            return result;
        }

        return new InstallPlan();

        InstallPlan? GetPlan(string from, string to, InstallPlan initialPlan)
        {
            // A malformed delta must not prevent trying the other routes, including a full patch.
            if (!visiting.Add(to))
                return null;

            try
            {
                return FindPlan(from, to, initialPlan);
            }
            finally
            {
                visiting.Remove(to);
            }
        }

        InstallPlan? FindPlan(string from, string to, InstallPlan initialPlan)
        {
            InstallPlan? bestPlan = null;

            if (patchesByOutput.TryGetValue(to, out var patches))
            {
                foreach (var patch in patches)
                {
                    if (patch.Requires.Version == from)
                    {
                        bestPlan = new InstallPlan();
                        bestPlan.ApplyPatchFile(new PatchDownloadRequest(feed, patch, downloadStorageDir), LocalizationService.Default.Message("Plan.ApplyPatchFromVersion", to, from));
                        break;
                    }

                    if (patch.Requires.IsBasePatch)
                    {
                        var testPlan = new InstallPlan();
                        testPlan.ApplyPatchFile(new PatchDownloadRequest(feed, patch, downloadStorageDir), LocalizationService.Default.Message("Plan.ApplyPatchFromPrepatch", to));
                        testPlan.Prepatch();
                        testPlan.RevertToStock();
                        if (bestPlan == null || bestPlan.Cost > testPlan.Cost) bestPlan = testPlan;
                    }
                    else
                    {
                        var newInitialPlan = new InstallPlan();
                        newInitialPlan.ApplyPatchFile(new PatchDownloadRequest(feed, patch, downloadStorageDir), LocalizationService.Default.Message("Plan.ApplyPatchFromVersion", to, patch.Requires.Version));
                        var testPlan = GetPlan(from, patch.Requires.Version!, newInitialPlan);
                        if (testPlan != null && (bestPlan == null || bestPlan.Cost > testPlan.Cost)) bestPlan = testPlan;
                    }
                }
            }

            if (bestPlan == null) return null;
            return bestPlan + initialPlan;
        }
    }

    /// <summary>
    /// Read a selected recipe contract without searching the legacy patch graph.
    /// </summary>
    /// <param name="version">The selected published version.</param>
    /// <returns>The deferred authority-bound recipe plan, or null for a legacy or unknown version.</returns>
    public InstallPlan? GetFullSourcePlan(GameVersion version) => GetFullSourcePlan(version, repair: false);

    internal InstallPlan? GetFullSourcePlan(GameVersion version, bool repair)
    {
        string targetVersion = $"{version.VersionNumber}.{version.BuildNumber}";
        var descriptor = _manifest?.Patches.SingleOrDefault(patch => patch.Version == targetVersion && patch.FullSource is not null)?.FullSource;
        return descriptor is null ? null : new InstallPlan
        {
            FullSourceReleaseId = descriptor.ReleaseId, FullSourceChannel = CurrentChannel,
            FullSourceVersion = targetVersion, FullSourceDownloadBytes = descriptor.DownloadBytes,
            FullSourceFeed = feed.SnapshotAuthority(), FullSourceManifestSha256 = descriptor.TargetManifestSha256,
            FullSourceAllowsDerivedBootConfig = descriptor.AllowsDerivedBootConfig, IsFullSourceRepair = repair
        };
    }

}
