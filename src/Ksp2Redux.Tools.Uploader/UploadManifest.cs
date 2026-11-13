using Ksp2Redux.Tools.Common.Models;

namespace Ksp2Redux.Tools.Uploader;

public sealed record UploadManifest
{
    public required string Repository { get; init; }
    public required string Branch { get; init; }
    public required string File { get; init; }
    public required string Token { get; init; }
    public required string Version { get; init; }

    public bool Prerelease { get; init; }

    public string? Label { get; init; }
    public string? Changelog { get; init; }

    // A swinfo document in the same repository whose version field tracks this
    // publication. Omitted for channels that do not publish one, in which case
    // no swinfo file is touched.
    public string? SwinfoFile { get; init; }

    public List<PatchUploadEntry>? Patches { get; init; }
    public string? FullSourcePublication { get; init; }

    public string? Channel { get; init; }
    public string? R2AccountId { get; init; }
    public string? R2Bucket { get; init; }
    public string? R2AccessKeyId { get; init; }
    public string? R2SecretAccessKey { get; init; }
    public string? R2Endpoint { get; init; }
    public string R2PublicBaseUrl { get; init; } = "https://download.ksp2redux.org";
    public string? CloudflareZoneId { get; init; }
    public string? CloudflareApiToken { get; init; }
    public int RetainBetaReleases { get; init; } = 5;
    public bool MigrateExisting { get; init; }
    public bool StageOnly { get; init; }
    public bool R2ManifestOnly { get; init; }

    /// <summary>
    /// Rejects same-version deltas before any release assets are changed.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when a delta requires its own output version.</exception>
    public void ValidatePatchRequirements()
    {
        if (Patches?.Any(patch => string.Equals(patch.PreviousVersion, Version, StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidDataException($"Cannot publish a delta from {Version} to itself.");
    }

    /// <summary>
    /// Replaces previous entries for this publication and applies the channel's retention policy.
    /// </summary>
    /// <param name="feed">The feed to update.</param>
    /// <param name="additions">The patches uploaded by this publication.</param>
    /// <returns>The entries removed from the feed.</returns>
    public List<ReleasePatch> ApplyRetention(ReleaseManifest feed, List<ReleasePatch> additions)
    {
        var removed = new List<ReleasePatch>();
        if (Channel is "stable" or "beta")
        {
            removed.AddRange(feed.Patches.Where(patch => patch.Version == Version));
            feed.Patches = additions.Concat(feed.Patches.Except(removed)).ToList();
            if (Channel == "beta")
            {
                var retainedVersions = feed.Patches
                    .GroupBy(patch => patch.Version, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Max(patch => patch.ReleasedAt))
                    .Take(Math.Max(1, RetainBetaReleases))
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rotated = feed.Patches.Where(patch => !retainedVersions.Contains(patch.Version)).ToList();
                removed.AddRange(rotated);
                feed.Patches = feed.Patches.Where(patch => retainedVersions.Contains(patch.Version)).ToList();
            }
            return removed;
        }

        // A retry may replace an asset under the same URL, so none of its old routes can survive.
        var stale = feed.Patches.Where(patch =>
            (additions.Count > 0 && string.Equals(patch.Version, Version, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(Label) && patch.Label == Label)).ToList();
        removed.AddRange(stale);
        feed.Patches = additions.Concat(feed.Patches.Except(stale)).ToList();
        return removed;
    }
}
