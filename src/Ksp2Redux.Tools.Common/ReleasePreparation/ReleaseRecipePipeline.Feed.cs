using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Common.ReleasePreparation;

public partial class ReleaseRecipePipeline
{
    /// <summary>
    /// Read bounded, verified artifact metadata before fetching its independently hashed packs.
    /// </summary>
    /// <param name="artifactRoot">The directory containing release.json.</param>
    /// <param name="expectedArtifactSha256">The independently published artifact digest.</param>
    /// <param name="expectedTargetSha256">The independently published target contract digest.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The validated metadata without claiming baseline or pack verification.</returns>
    public ReleaseRecipeManifest Inspect(string artifactRoot, string expectedArtifactSha256,
        string expectedTargetSha256, CancellationToken ct = default)
        => ReadManifest(Child(Root(artifactRoot), "release.json"), expectedArtifactSha256, expectedTargetSha256, ct, out _);

    /// <summary>
    /// Verify a downloaded release manifest in its existing cache file.
    /// </summary>
    /// <param name="releaseFile">The canonical path to the downloaded manifest.</param>
    /// <param name="expectedLength">The manifest byte count from the release feed.</param>
    /// <param name="expectedArtifactSha256">The expected manifest digest from the release feed.</param>
    /// <param name="expectedTargetSha256">The expected target contract digest from the release feed.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The validated metadata without claiming pack or baseline verification.</returns>
    public ReleaseRecipeManifest InspectDownloaded(string releaseFile, long expectedLength, string expectedArtifactSha256,
        string expectedTargetSha256, CancellationToken ct = default)
        => ReadManifest(Root(releaseFile), expectedArtifactSha256, expectedTargetSha256, ct, out _, expectedLength);

    private ReleaseRecipeManifest ReadManifest(string releaseFile, string expectedArtifactSha256,
        string expectedTargetSha256, CancellationToken ct, out StockBaselineManifest target, long? expectedLength = null)
    {
        ct.ThrowIfCancellationRequested();
        using Stream marker = OpenRead(releaseFile);
        if (marker.Length > MAX_MANIFEST_BYTES)
            throw new InvalidDataException("Release metadata exceeds its byte budget.");
        Verify(marker, expectedLength ?? marker.Length, expectedArtifactSha256, ct);
        ReleaseRecipeManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleaseRecipeManifest>(marker, COMPACT_JSON_OPTIONS)
                ?? throw new InvalidDataException("Release metadata is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid release metadata JSON.", exception);
        }
        if (manifest.TargetManifest is null || !EqualHash(Digest(manifest.TargetManifest), expectedTargetSha256))
            throw new InvalidDataException("Release target contract digest mismatch.");
        target = StockBaselineManifest.FromJson(manifest.TargetManifest);
        ValidateManifest(manifest, target);
        return manifest;
    }

    /// <summary>
    /// Create a local schema-three feed entry from an already published artifact and explicit publisher selectors.
    /// </summary>
    /// <remarks>
    /// This creates metadata for review and later publication. Local hashes do not authenticate it.
    /// Neither files nor remote feeds are changed. Baseline definitions must exactly match artifact variants.
    /// </remarks>
    /// <param name="artifactRoot">The local artifact directory.</param>
    /// <param name="publication">The completed local publication measurements.</param>
    /// <param name="releaseId">The immutable publisher release selector.</param>
    /// <param name="channel">The containing release channel.</param>
    /// <param name="version">The five-component game version and build identity.</param>
    /// <param name="r2ArtifactUrl">The future primary HTTPS release.json URL.</param>
    /// <param name="githubArtifactUrl">The future backup HTTPS release.json URL.</param>
    /// <param name="baselines">The complete publisher-curated baselines and optional exact Steam selectors.</param>
    /// <param name="allowsDerivedBootConfig">True only when the publisher permits boot derivation.</param>
    /// <param name="releasedAt">The intended release timestamp.</param>
    /// <param name="updateSourceDigests">The complete prior Redux release contract digests, or null for stock-only recipes.</param>
    /// <returns>The validated feed entry for local serialization and review.</returns>
    public ReleasePatch CreateFeedEntry(string artifactRoot, ReleaseRecipePublication publication,
        string releaseId, string channel, string version, string r2ArtifactUrl, string githubArtifactUrl,
        List<FullSourceBaselineDefinition> baselines, bool allowsDerivedBootConfig, DateTime releasedAt,
        List<string>? updateSourceDigests = null)
    {
        ReleaseRecipeManifest manifest = Inspect(artifactRoot, publication.ArtifactSha256, publication.TargetManifestSha256);
        long size = _fileSystem.FileInfo.New(Child(artifactRoot, "release.json")).Length;
        var descriptor = new FullSourceReleaseDescriptor(releaseId, channel, version, manifest.TargetManifest,
            publication.TargetManifestSha256, r2ArtifactUrl, githubArtifactUrl, baselines,
            allowsDerivedBootConfig, checked(size + manifest.Packs.Sum(pack => (long)pack.Length)))
        {
            UpdateSourceDigests = updateSourceDigests,
        };
        var patch = new ReleasePatch
        {
            Version = version, Requires = new PatchRequirement { Version = null }, ReleasedAt = releasedAt,
            ChecksumSha256 = publication.ArtifactSha256, Size = size,
            FullSource = descriptor,
        };
        ReleaseManifestValidator.ValidatePatch(patch);
        ValidateFeedArtifact(manifest, descriptor, size);
        return patch;
    }

    /// <summary>
    /// Verify that an inspected artifact agrees with its independently supplied feed descriptor.
    /// </summary>
    /// <param name="manifest">The inspected artifact metadata.</param>
    /// <param name="descriptor">The release descriptor from the selected feed.</param>
    /// <param name="markerLength">The independently expected marker length.</param>
    public static void ValidateFeedArtifact(ReleaseRecipeManifest manifest, FullSourceReleaseDescriptor descriptor, long markerLength)
    {
        var expectedDigests = descriptor.Baselines.Select(baseline => Digest(baseline.Manifest))
            .Concat(descriptor.UpdateSourceDigests ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!manifest.TargetManifest.AsSpan().SequenceEqual(descriptor.TargetManifest) ||
            expectedDigests.Count != manifest.Variants.Count ||
            manifest.Variants.Any(variant => !expectedDigests.Contains(Digest(variant.SourceManifest))) ||
            checked(markerLength + manifest.Packs.Sum(pack => (long)pack.Length)) != descriptor.DownloadBytes)
            throw new InvalidDataException("Artifact target, source contracts or byte counts differ from feed metadata.");
    }

    /// <summary>
    /// Verify that a separate update recipe covers only the expected installed source and target.
    /// </summary>
    /// <param name="manifest">The inspected update artifact metadata.</param>
    /// <param name="target">The complete expected target contract.</param>
    /// <param name="artifact">The authenticated separate update artifact descriptor.</param>
    public static void ValidateUpdateFeedArtifact(ReleaseRecipeManifest manifest, byte[] target, FullSourceUpdateArtifact artifact)
    {
        if (!manifest.TargetManifest.AsSpan().SequenceEqual(target) || manifest.Variants.Count != 1 ||
            !EqualHash(Digest(manifest.Variants[0].SourceManifest), artifact.SourceDigest) ||
            checked(artifact.MarkerSize + manifest.Packs.Sum(pack => (long)pack.Length)) != artifact.DownloadBytes)
            throw new InvalidDataException("Separate update target, source or byte counts differ from feed metadata.");
    }

    /// <summary>
    /// Get the source contract carried by an inspected recipe variant with the specified digest.
    /// </summary>
    /// <param name="manifest">The inspected artifact metadata.</param>
    /// <param name="sourceDigest">The independently expected source contract digest.</param>
    /// <returns>The variant's complete source contract.</returns>
    public static StockBaselineManifest GetSourceContract(ReleaseRecipeManifest manifest, string sourceDigest)
    {
        var variant = manifest.Variants.SingleOrDefault(variant => EqualHash(Digest(variant.SourceManifest), sourceDigest))
            ?? throw new InvalidDataException("No recipe variant matches the requested source contract digest.");
        var source = StockBaselineManifest.FromJson(variant.SourceManifest);
        new StockArchiveLimits().Validate(source);
        if (!source.ToJson().AsSpan().SequenceEqual(variant.SourceManifest))
            throw new InvalidDataException("The update source contract is not canonical.");
        return source;
    }
}
