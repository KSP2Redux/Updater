using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.BundleConversion.Bundles;
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
        var normalized = WithoutBootConfig(manifest);
        ValidateManifest(normalized, target);
        return target.DroppedBootConfig is { Length: <= BootConfigFile.MAX_BYTES } historical
            ? normalized with { HistoricalBootRecipe = FindHistoricalBootRecipe(manifest, historical) }
            : normalized;
    }

    private static ReleaseFileRecipe? FindHistoricalBootRecipe(ReleaseRecipeManifest manifest, StockBaselineFile expected)
    {
        // Stock and installed-source variants describe the exact same target. Prefer an independent literal recipe
        // whenever any variant provides one. An exact copy or a recipe that needs sources is not independent evidence.
        foreach (var file in manifest.Variants.SelectMany(variant => variant.Files))
        {
            if (file is null || !BootConfigFile.IsExcluded(file.Path) ||
                file.Recipe is not { Version: BundleRecipe.RAW_TARGET_VERSION } recipe ||
                recipe.Entries is not { Count: 1 } || recipe.Entries[0]?.Segments is not { } segments ||
                segments.Any(segment => segment is null || segment.Kind != BundleRecipeSegmentKind.Payload))
                continue;
            if (recipe.TargetLength != expected.Length || !EqualHash(recipe.TargetSha256, expected.Sha256) ||
                recipe.PayloadLength > BootConfigFile.MAX_BYTES || file.PayloadChunks is null ||
                file.PayloadChunks.Count > MAX_FILE_RECORDS ||
                file.PayloadChunks.Any(index => index < 0 || index >= manifest.Chunks.Count) ||
                file.PayloadChunks.Sum(index => (long)manifest.Chunks[index].Length) != recipe.PayloadLength)
                throw new InvalidDataException("The historical release boot.config recipe differs from its target contract.");
            recipe.ValidateStructure();
            return file;
        }
        return null;
    }

    /// <summary>Get the pack indices needed for an independently reconstructible historical boot.config.</summary>
    /// <param name="manifest">The authenticated artifact metadata returned by inspection.</param>
    /// <returns>The pack indices in manifest order, or null when no literal recipe is available.</returns>
    public static int[]? GetHistoricalBootPackIndices(ReleaseRecipeManifest manifest) =>
        manifest.HistoricalBootRecipe?.PayloadChunks.Select(index => manifest.Chunks[index].PackIndex).Distinct().Order().ToArray();

    /// <summary>Recover the authenticated historical boot.config output in memory.</summary>
    /// <param name="manifest">The authenticated artifact metadata returned by inspection.</param>
    /// <param name="packFiles">The verified pack paths in manifest order.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The exact bytes checked against the historical target identity.</returns>
    public byte[] ReadHistoricalBootConfig(ReleaseRecipeManifest manifest, IReadOnlyList<string?> packFiles, CancellationToken ct)
    {
        var file = manifest.HistoricalBootRecipe
            ?? throw new InvalidDataException("This artifact has no independent historical boot.config recipe.");
        using var reader = new PackReader(this, manifest, index => packFiles[index]
            ?? throw new InvalidDataException("The historical boot.config pack was not downloaded."));
        using var payload = new ReleasePayloadStream(reader, manifest, file.PayloadChunks, ct);
        using var output = new MemoryStream((int)file.Recipe.TargetLength);
        ct.ThrowIfCancellationRequested();
        file.Recipe.Reconstruct(new Stream?[file.Recipe.Sources.Count], payload, output);
        return output.ToArray();
    }

    // A release published before boot.config left every contract also carries a recipe or an exact copy for that file,
    // and lists it as a recipe source, which every release built by the CI since 14 November 2026 does. Contracts read
    // without it, so the artifact reads without it too. A source nothing reads from is dropped. A recipe that reads
    // bytes from it cannot be reconstructed, so the artifact is refused. Every other recipe is checked as before.
    private static ReleaseRecipeManifest WithoutBootConfig(ReleaseRecipeManifest manifest)
    {
        static bool Lists(ReleaseRecipeVariant? variant) =>
            variant is not null && (variant.Files?.Any(file => file is not null && BootConfigFile.IsExcluded(file.Path)) == true ||
                variant.ExactCopies?.Keys.Any(BootConfigFile.IsExcluded) == true ||
                variant.Sources?.Any(source => source is not null && BootConfigFile.IsExcluded(source.Path)) == true);
        if (manifest.Variants is null || !manifest.Variants.Any(Lists))
            return manifest;
        return manifest with
        {
            Variants = manifest.Variants.Select(variant => !Lists(variant) ? variant : WithoutBootConfig(variant!)).ToList()
        };
    }

    private static ReleaseRecipeVariant WithoutBootConfig(ReleaseRecipeVariant variant)
    {
        var files = variant.Files?.Where(file => file is null || !BootConfigFile.IsExcluded(file.Path)).ToList()!;
        var sources = variant.Sources;
        if (sources is not null)
        {
            var dropped = Enumerable.Range(0, sources.Count)
                .Where(index => sources[index] is not null && BootConfigFile.IsExcluded(sources[index].Path)).ToHashSet();
            if (dropped.Count > 0)
            {
                files = files?.Select(file => WithoutSources(file, dropped, sources.Count)).ToList()!;
                sources = sources.Where((_, index) => !dropped.Contains(index)).ToList();
            }
        }
        return variant with
        {
            Sources = sources!,
            Files = files,
            ExactCopies = variant.ExactCopies?.Where(copy => !BootConfigFile.IsExcluded(copy.Key))
                .ToDictionary(copy => copy.Key, copy => copy.Value)
        };
    }

    // Removes the dropped variant sources from one recipe and renumbers the rest. A recipe whose shape does not fit its
    // variant is returned unchanged, so the ordinary validation refuses it.
    private static ReleaseFileRecipe? WithoutSources(ReleaseFileRecipe? file, HashSet<int> dropped, int total)
    {
        var recipe = file?.Recipe;
        if (file is null || recipe?.Sources is null)
            return file;
        int[] indices = file.SourceIndices ?? Enumerable.Range(0, recipe.Sources.Count).ToArray();
        if (indices.Length != recipe.Sources.Count || (file.SourceIndices is null && indices.Length != total) ||
            indices.Any(index => index < 0 || index >= total))
            return file;
        int Renumber(int index) => index - dropped.Count(other => other < index);
        var local = Enumerable.Range(0, indices.Length).Where(position => dropped.Contains(indices[position])).ToHashSet();
        if (local.Count > 0)
        {
            if (recipe.Entries?.Any(entry => entry?.Segments?.Any(segment =>
                    segment is not null && segment.Kind == BundleRecipeSegmentKind.Source && local.Contains(segment.SourceIndex)) == true) == true)
                throw new InvalidDataException(
                    "This release needs boot.config as a recipe input and cannot be installed by this launcher.");
            foreach (var segment in recipe.Entries?.Where(entry => entry?.Segments is not null).SelectMany(entry => entry.Segments)
                ?? [])
            {
                if (segment is not null && segment.Kind == BundleRecipeSegmentKind.Source)
                    segment.SourceIndex -= local.Count(position => position < segment.SourceIndex);
            }
            recipe.Sources = recipe.Sources.Where((_, position) => !local.Contains(position)).ToList();
        }
        return file.SourceIndices is null ? file
            : file with { SourceIndices = indices.Where((_, position) => !local.Contains(position)).Select(Renumber).ToArray() };
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
    /// <param name="releasedAt">The intended release timestamp.</param>
    /// <param name="updateSourceDigests">The complete prior Redux release contract digests, or null for stock-only recipes.</param>
    /// <param name="prepatchManifest">The pinned common prepatch contract, or null for original stock recipes.</param>
    /// <param name="prepatchResources">The embedded package digests for the common prepatch, or null.</param>
    /// <param name="bootConfigSettings">The boot.config keys and values of the release's player, or null.</param>
    /// <returns>The validated feed entry for local serialization and review.</returns>
    public ReleasePatch CreateFeedEntry(string artifactRoot, ReleaseRecipePublication publication,
        string releaseId, string channel, string version, string r2ArtifactUrl, string githubArtifactUrl,
        List<FullSourceBaselineDefinition> baselines, DateTime releasedAt,
        List<string>? updateSourceDigests = null, byte[]? prepatchManifest = null,
        Dictionary<string, string>? prepatchResources = null, Dictionary<string, string>? bootConfigSettings = null)
    {
        ReleaseRecipeManifest manifest = Inspect(artifactRoot, publication.ArtifactSha256, publication.TargetManifestSha256);
        long size = _fileSystem.FileInfo.New(Child(artifactRoot, "release.json")).Length;
        var descriptor = new FullSourceReleaseDescriptor(releaseId, channel, version, manifest.TargetManifest,
            publication.TargetManifestSha256, r2ArtifactUrl, githubArtifactUrl, baselines,
            checked(size + manifest.Packs.Sum(pack => (long)pack.Length)))
        {
            BootConfigSettings = bootConfigSettings,
            UpdateSourceDigests = updateSourceDigests,
            PrepatchManifest = prepatchManifest,
            PrepatchManifestSha256 = prepatchManifest is null ? null : Digest(prepatchManifest),
            PrepatchResources = prepatchResources,
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
        var prepatch = ReleaseManifestValidator.GetPrepatchBaseline(descriptor);
        // Contracts of releases published before boot.config left them list it. A variant is identified by the digest of
        // its normalized contract. A listed update source may name either form of it.
        var expectedDigests = (prepatch is null ? descriptor.Baselines.Select(baseline => NormalizedDigest(baseline.Manifest))
            : new[] { Digest(prepatch.ToJson()) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variantDigests = manifest.Variants.Select(variant => NormalizedDigest(variant.SourceManifest))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string listed in descriptor.UpdateSourceDigests ?? [])
        {
            string? matching = manifest.Variants.Where(variant => EqualHash(Digest(variant.SourceManifest), listed) ||
                EqualHash(NormalizedDigest(variant.SourceManifest), listed)).Select(variant => NormalizedDigest(variant.SourceManifest))
                .FirstOrDefault();
            expectedDigests.Add(matching ?? listed);
        }
        if (!NormalizedTarget(manifest.TargetManifest).AsSpan().SequenceEqual(descriptor.GetNormalizedTargetManifest()) ||
            variantDigests.Count != manifest.Variants.Count || expectedDigests.Count != variantDigests.Count ||
            variantDigests.Any(digest => !expectedDigests.Contains(digest)) ||
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
        if (!NormalizedTarget(manifest.TargetManifest).AsSpan().SequenceEqual(NormalizedTarget(target)) || manifest.Variants.Count != 1 ||
            !(EqualHash(Digest(manifest.Variants[0].SourceManifest), artifact.SourceDigest) ||
                EqualHash(NormalizedDigest(manifest.Variants[0].SourceManifest), artifact.SourceDigest)) ||
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
        var variant = manifest.Variants.SingleOrDefault(variant => EqualHash(Digest(variant.SourceManifest), sourceDigest) ||
                EqualHash(NormalizedDigest(variant.SourceManifest), sourceDigest))
            ?? throw new InvalidDataException("No recipe variant matches the requested source contract digest.");
        var source = StockBaselineManifest.FromJson(variant.SourceManifest);
        new StockArchiveLimits().Validate(source);
        if (!source.IsCanonical(variant.SourceManifest))
            throw new InvalidDataException("The update source contract is not canonical.");
        return source;
    }

    private static byte[] NormalizedTarget(byte[] contract) => StockBaselineManifest.FromJson(contract).ToJson();

    private static string NormalizedDigest(byte[] contract) => Digest(NormalizedTarget(contract));
}
