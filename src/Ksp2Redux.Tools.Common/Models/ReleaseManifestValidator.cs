using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Common.Models;

/// <summary>
/// Validates release-manifest versions and patch transport definitions.
/// </summary>
public static class ReleaseManifestValidator
{
    /// <summary>
    /// Select the internal recipe feed without modifying a shared launcher configuration.
    /// </summary>
    /// <param name="path">The configured repository-relative feed path.</param>
    /// <returns>The separate internal recipe path, or the unchanged public/custom path.</returns>
    public static string InternalRecipeFeedPath(string path)
    {
        int separator = path.LastIndexOf('/');
        string name = path[(separator + 1)..];
        string recipe = name.ToLowerInvariant() switch
        {
            "manifest.develop.json" => "Manifest.Develop.v3.json",
            "manifest.next.json" => "Manifest.Next.v3.json",
            "manifest-mr.json" => "manifest-mr.v3.json",
            _ => name
        };
        return path[..(separator + 1)] + recipe;
    }

    /// <summary>
    /// Reject a recipe publication that could replace a legacy launcher feed.
    /// </summary>
    /// <param name="path">The intended repository-relative destination.</param>
    /// <exception cref="InvalidDataException">Thrown when the destination is not a separate recipe feed.</exception>
    public static void RequireRecipeFeedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".v3.json", StringComparison.OrdinalIgnoreCase) ||
            path.Split('/') is { Length: > 32 } || path.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.Contains('\\')))
            throw new InvalidDataException("Full-source publication requires a separate .v3.json recipe feed. Legacy feeds cannot be rewritten.");
    }

    /// <summary>
    /// The maximum serialized size of an authenticated online release feed.
    /// </summary>
    public const int MAX_ONLINE_FEED_BYTES = 64 * 1024 * 1024;

    /// <summary>
    /// Validates a release manifest before it is used or published.
    /// </summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <exception cref="InvalidDataException">Thrown when the manifest is unsupported or inconsistent.</exception>
    public static void Validate(ReleaseManifest manifest)
        => Validate(manifest, hosted: false, requireUnixMetadata: false);

    /// <summary>
    /// Validate a hosted feed and require complete game contracts for its full-source releases.
    /// </summary>
    /// <param name="manifest">The hosted release manifest.</param>
    /// <param name="requireUnixMetadata">Require Unix permissions for the target and stock contracts.</param>
    /// <exception cref="InvalidDataException">Thrown when the feed or a game contract is incomplete or inconsistent.</exception>
    public static void ValidateHosted(ReleaseManifest manifest, bool requireUnixMetadata = false)
        => Validate(manifest, hosted: true, requireUnixMetadata: requireUnixMetadata);

    /// <summary>
    /// Require the game paths needed by the hosted full-source launcher.
    /// </summary>
    /// <param name="manifest">The target or baseline game contract.</param>
    /// <param name="requireUnixMetadata">Require Unix permissions for the game tree.</param>
    /// <exception cref="InvalidDataException">Thrown when required game paths or Unix permissions are absent.</exception>
    public static void ValidateHostedGameContract(StockBaselineManifest manifest, bool requireUnixMetadata = false)
    {
        if (requireUnixMetadata && !manifest.HasUnixMetadata)
            throw new InvalidDataException("Linux release and stock definitions require complete version-two Unix metadata.");
        if (!manifest.Files.Any(file => file.Path == "KSP2_x64.exe") ||
            !manifest.Files.Any(file => file.Path == "KSP2_x64_Data/boot.config") ||
            !manifest.Files.Any(file => file.Path.StartsWith("KSP2_x64_Data/StreamingAssets/", StringComparison.Ordinal)))
            throw new InvalidDataException("Production full-source metadata must declare executable, original boot configuration and StreamingAssets coverage.");
    }

    private static void Validate(ReleaseManifest manifest, bool hosted, bool requireUnixMetadata)
    {
        if (manifest.SchemaVersion is < 1 or > 3)
            throw new InvalidDataException(
                $"Release manifest schema {manifest.SchemaVersion} is not supported. Update the KSP2 Redux launcher.");

        if (string.IsNullOrWhiteSpace(manifest.Channel))
            throw new InvalidDataException("The release manifest does not identify its channel.");

        if (manifest.Patches == null)
            throw new InvalidDataException("The release manifest has no patch list.");

        foreach (var patch in manifest.Patches)
        {
            if (patch.FullSource is { } descriptor)
            {
                if (manifest.SchemaVersion < 3 || descriptor.Channel != manifest.Channel)
                    throw new InvalidDataException("Full-source releases require schema 3 and their containing channel.");
            }
            if (manifest.SchemaVersion == 1 && patch.Chunks is not null)
                throw new InvalidDataException("Chunked patches require release manifest schema 2.");
            ValidatePatch(patch, hosted, requireUnixMetadata);
        }
    }

    /// <summary>
    /// Validates one release patch transport definition.
    /// </summary>
    /// <param name="patch">The patch to validate.</param>
    /// <exception cref="InvalidDataException">Thrown when the patch transport is inconsistent.</exception>
    public static void ValidatePatch(ReleasePatch patch)
        => ValidatePatch(patch, hosted: false, requireUnixMetadata: false);

    private static void ValidatePatch(ReleasePatch patch, bool hosted, bool requireUnixMetadata)
    {
        if (patch.FullSource is { } descriptor)
        {
            ValidateFullSource(patch, descriptor, hosted, requireUnixMetadata);
            return;
        }
        bool hasLegacyUrl = !string.IsNullOrWhiteSpace(patch.Url);
        bool hasChunks = patch.Chunks is not null;
        if (hasLegacyUrl == hasChunks)
            throw new InvalidDataException(
                $"Patch {patch.Version} must contain either one legacy URL or a chunk list, but not both.");

        if (patch.Size <= 0)
            throw new InvalidDataException($"Patch {patch.Version} has an invalid size.");

        if (!IsSha256(patch.ChecksumSha256))
            throw new InvalidDataException($"Patch {patch.Version} has an invalid SHA-256 checksum.");

        if (!hasChunks)
            return;

        if (patch.Chunks!.Count == 0)
            throw new InvalidDataException($"Patch {patch.Version} has an empty chunk list.");

        long totalSize = 0;
        for (var index = 0; index < patch.Chunks.Count; index++)
        {
            var chunk = patch.Chunks[index];
            if (chunk.Index != index)
                throw new InvalidDataException($"Patch {patch.Version} has a missing or duplicate chunk index at {index}.");
            if (chunk.Size <= 0)
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} has an invalid size.");
            if (!IsSha256(chunk.ChecksumSha256))
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} has an invalid SHA-256 checksum.");
            if (!IsHttpUrl(chunk.GitHubUrl))
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} does not have a GitHub download URL.");
            if (chunk.R2Url is not null && !IsHttpUrl(chunk.R2Url))
                throw new InvalidDataException($"Patch {patch.Version} chunk {index} has an invalid R2 download URL.");

            checked
            {
                totalSize += chunk.Size;
            }
        }

        if (totalSize != patch.Size)
            throw new InvalidDataException(
                $"Patch {patch.Version} declares {patch.Size} bytes but its chunks contain {totalSize} bytes.");
    }

    private static void ValidateFullSource(ReleasePatch patch, FullSourceReleaseDescriptor descriptor, bool hosted, bool requireUnixMetadata)
    {
        if (patch.Url is not null || patch.Chunks is not null || patch.Size is <= 0 or > ReleaseRecipePipeline.MAX_MANIFEST_BYTES ||
            !IsSha256(patch.ChecksumSha256) || descriptor.Version != patch.Version ||
            string.IsNullOrWhiteSpace(descriptor.ReleaseId) || descriptor.ReleaseId.Length > 4096 ||
            string.IsNullOrWhiteSpace(descriptor.Channel) || descriptor.Channel.Length > 4096 ||
            descriptor.Version.Split('.').Length != 5 ||
            descriptor.Version.Split('.').Any(part => !uint.TryParse(part, out _)) ||
            !IsSha256(descriptor.TargetManifestSha256) || descriptor.Baselines is null ||
            descriptor.Baselines.Count is < 1 or > 16 || descriptor.DownloadBytes < patch.Size ||
            descriptor.DownloadBytes > 128L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid full-source release identity or transport bounds.");
        ValidateArtifactUrl(descriptor.R2ArtifactUrl);
        ValidateArtifactUrl(descriptor.GitHubArtifactUrl);
        var target = Restoration.StockBaselineManifest.FromJson(descriptor.TargetManifest);
        if (!target.ToJson().AsSpan().SequenceEqual(descriptor.TargetManifest) ||
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(descriptor.TargetManifest)) !=
            descriptor.TargetManifestSha256.ToUpperInvariant())
            throw new InvalidDataException("The full-source target contract or digest is not canonical.");
        if (hosted)
            ValidateHostedGameContract(target, requireUnixMetadata);
        if (GetPrepatchBaseline(descriptor) is not null && requireUnixMetadata)
            throw new InvalidDataException("Embedded prepatch normalization is a Windows release input.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var sourceDigests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseline in descriptor.Baselines)
        {
            var stock = Restoration.StockBaselineManifest.FromJson(baseline.Manifest);
            if (!stock.ToJson().AsSpan().SequenceEqual(baseline.Manifest) || !identities.Add(stock.BaselineIdentity) ||
                baseline.SteamDepots is null || baseline.SteamDepots.Count > 16 ||
                baseline.SteamDepots.Any(depot => depot.DepotId == 0 || depot.ManifestId == 0) ||
                baseline.SteamDepots.Select(depot => depot.DepotId).Distinct().Count() != baseline.SteamDepots.Count)
                throw new InvalidDataException("Invalid or duplicate full-source stock definition.");
            if (hosted)
                ValidateHostedGameContract(stock, requireUnixMetadata);
            sourceDigests.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(baseline.Manifest)));
            if (baseline.LegacyOwnershipManifests is { } legacy)
            {
                if (legacy.Count is < 1 or > 32)
                    throw new InvalidDataException("Legacy ownership contracts exceed their count budget.");
                foreach (byte[] bytes in legacy)
                {
                    if (bytes is null)
                        throw new InvalidDataException("A legacy ownership contract is null.");
                    var owned = Restoration.StockBaselineManifest.FromJson(bytes);
                    new Restoration.StockArchiveLimits().Validate(owned);
                    if (!owned.ToJson().AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("A legacy ownership contract is noncanonical.");
                }
            }
        }
        if (descriptor.UpdateSourceDigests is { } updates &&
            (updates.Count == 0 || updates.Count > 16 - descriptor.Baselines.Count ||
             updates.Any(digest => !IsSha256(digest) || !sourceDigests.Add(digest))))
            throw new InvalidDataException("Invalid or duplicate full-source update contract digest.");
        if (descriptor.UpdateArtifacts is { } artifacts)
        {
            if (artifacts.Count == 0 || artifacts.Count + (descriptor.UpdateSourceDigests?.Count ?? 0) > 16 - descriptor.Baselines.Count)
                throw new InvalidDataException("Invalid separate update artifact count.");
            foreach (var artifact in artifacts)
            {
                if (artifact is null || !IsSha256(artifact.SourceDigest) || !sourceDigests.Add(artifact.SourceDigest) ||
                    !IsSha256(artifact.MarkerSha256) || artifact.MarkerSize is <= 0 or > ReleaseRecipePipeline.MAX_MANIFEST_BYTES ||
                    artifact.DownloadBytes < artifact.MarkerSize || artifact.DownloadBytes > 128L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Invalid or duplicate separate update artifact.");
                string marker = artifact.AssetPrefix + "release.json";
                ValidateArtifactUrl(artifact.R2ArtifactUrl, marker);
                ValidateArtifactUrl(artifact.GitHubArtifactUrl, marker);
            }
        }
    }

    /// <summary>
    /// Validate and read the publisher-pinned common prepatch contract.
    /// </summary>
    /// <param name="descriptor">The authenticated release descriptor.</param>
    /// <returns>The derived recipe baseline, or null for the original stock recipe format.</returns>
    /// <exception cref="InvalidDataException">Thrown when the prepatch contract or package pins are incomplete or invalid.</exception>
    public static StockBaselineManifest? GetPrepatchBaseline(FullSourceReleaseDescriptor descriptor)
    {
        if (descriptor.PrepatchManifest is null && descriptor.PrepatchManifestSha256 is null && descriptor.PrepatchResources is null)
            return null;
        if (descriptor.PrepatchManifest is not { Length: > 0 } bytes || !IsSha256(descriptor.PrepatchManifestSha256) ||
            descriptor.PrepatchResources is not { Count: 3 } resources ||
            !resources.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "Steam", "Epic", "Portable" }) ||
            resources.Values.Any(hash => !IsSha256(hash)) ||
            !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(
                descriptor.PrepatchManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The common prepatch contract and all three embedded package pins are required.");
        var contract = StockBaselineManifest.FromJson(bytes);
        if (contract.HasUnixMetadata || !contract.ToJson().AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("The prepatch baseline must be a canonical Windows contract.");
        ValidateHostedGameContract(contract);
        return contract;
    }

    private static void ValidateArtifactUrl(string value, string filename = "release.json")
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.EndsWith("/" + filename, StringComparison.Ordinal))
            throw new InvalidDataException("Full-source artifact URLs must name their expected marker over HTTPS without credentials.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";
}
