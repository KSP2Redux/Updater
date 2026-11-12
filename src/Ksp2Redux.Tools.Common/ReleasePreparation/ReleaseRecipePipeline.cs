using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using LZ4ps;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.ReleasePreparation;

/// <summary>
/// Publish exact recipes locally and reconstruct a verified complete release into an absent stage.
/// </summary>
/// <remarks>
/// The caller owns stable source trees and excludes concurrent writers to artifact and staging roots.
/// Digests supplied to Prepare must come from an independent trust decision. This component neither
/// authenticates releases nor fetches bytes, and never mutates an installation. Existing transaction
/// path checks reject links and aliases, without providing a sandbox against hostile concurrent writers.
/// </remarks>
public partial class ReleaseRecipePipeline
{
    /// <summary>
    /// The pinned pack layout and compression implementation. Changes require a new profile.
    /// </summary>
    public const string PACK_PROFILE = "concat-chunks-128k-lz4hc-assetstools-3.0.5-raw-if-not-smaller-v1";
    /// <summary>
    /// The same literal encoding grouped into larger packs for hosted releases.
    /// </summary>
    public const string HOSTED_PACK_PROFILE = "concat-chunks-128k-lz4hc-assetstools-3.0.5-raw-if-not-smaller-pack64m-v2";
    /// <summary>
    /// The maximum stored bytes per independently hashed pack.
    /// </summary>
    public const int MAX_PACK_BYTES = 8 * 1024 * 1024;
    /// <summary>
    /// The maximum byte size of a pack in a hosted release.
    /// </summary>
    public const int MAX_HOSTED_PACK_BYTES = 64 * 1024 * 1024;
    /// <summary>
    /// The hosted pack count after reserving one release asset for release.json.
    /// </summary>
    public const int MAX_HOSTED_PACKS = 999;
    /// <summary>
    /// The maximum decoded bytes per chunk.
    /// </summary>
    public const int MAX_CHUNK_BYTES = 128 * 1024;
    /// <summary>
    /// The bounded release manifest size before parsing or publication.
    /// </summary>
    public const int MAX_MANIFEST_BYTES = 128 * 1024 * 1024;
    private const int MAX_RELEASE_CHUNKS = 1048576;
    private const int MAX_FILE_RECORDS = 262144;
    private const int COMPACT_METADATA_VERSION = 2;
    private const int EXACT_COPY_VERSION = 3;
    private readonly IFileSystem _fileSystem;
    private readonly InstallTransactionEngine _pathValidator;
    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        AllowDuplicateProperties = false,
    };
    private static readonly JsonSerializerOptions COMPACT_JSON_OPTIONS = new(JSON_OPTIONS)
    {
        Converters = { new ChunkRecordConverter() },
    };

    /// <summary>
    /// Construct a local publisher and staging reader using the repository filesystem abstraction.
    /// </summary>
    /// <param name="fileSystem">The filesystem used for all access.</param>
    /// <param name="validateFileStreams">The stream validator for a virtual filesystem, or null for native checks.</param>
    public ReleaseRecipePipeline(IFileSystem fileSystem, Action<string>? validateFileStreams = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        if (fileSystem is RealFileSystem && validateFileStreams is not null)
            throw new ArgumentException("Native release preparation requires native file stream validation.", nameof(validateFileStreams));
        _pathValidator = new InstallTransactionEngine(fileSystem, validateFileStreams: validateFileStreams);
    }

    /// <summary>
    /// Get the pack indices needed to reconstruct a verified release from one baseline.
    /// </summary>
    /// <param name="manifest">The artifact metadata returned by inspection.</param>
    /// <param name="sourceContract">The independently expected source contract.</param>
    /// <param name="retainedTargetPaths">Exact target paths already supplied by the installation, if any.</param>
    /// <returns>The required pack indices in manifest order.</returns>
    public static int[] GetRequiredPackIndices(ReleaseRecipeManifest manifest, StockBaselineManifest sourceContract,
        IReadOnlySet<string>? retainedTargetPaths = null)
        => RequiredPacks(manifest, SelectVariant(manifest, sourceContract), retainedTargetPaths).Order().ToArray();

    /// <summary>
    /// Get the exact source files read by a recipe, including complete file copies.
    /// </summary>
    /// <param name="manifest">The artifact metadata returned by inspection.</param>
    /// <param name="sourceContract">The independently expected complete source contract.</param>
    /// <param name="retainedTargetPaths">Exact target paths already supplied by the installation, if any.</param>
    /// <returns>The recipe input contract with the original directory layout.</returns>
    public static StockBaselineManifest GetRecipeInputContract(ReleaseRecipeManifest manifest, StockBaselineManifest sourceContract,
        IReadOnlySet<string>? retainedTargetPaths = null)
        => RecipeInputContract(SelectVariant(manifest, sourceContract), sourceContract, retainedTargetPaths);

    /// <summary>
    /// Publish a complete frozen target and supported baseline variants into a new local directory.
    /// </summary>
    /// <remarks>
    /// Every target defaults to raw-file-exact-v1, preserving native build bytes. Explicit canonical
    /// paths must already match the pinned canonical UnityFS profile or publication fails. No target
    /// normalization silently changes the supplied tree contract. One file is generated at a time.
    /// A failed output remains caller-owned diagnostic scratch and has no release.json commit marker.
    /// </remarks>
    /// <param name="targetRoot">The stable complete target tree.</param>
    /// <param name="target">The expected complete target contract frozen before generation.</param>
    /// <param name="baselines">One to sixteen independently described baseline variants.</param>
    /// <param name="outputRoot">An absent local output directory with an existing parent.</param>
    /// <param name="canonicalPaths">Explicit already-canonical UnityFS target paths, or null for raw files.</param>
    /// <param name="ct">The cancellation token checked between files and chunks.</param>
    /// <returns>The measured payload and source reuse sizes and unauthenticated local digests.</returns>
    public ReleaseRecipePublication Publish(string targetRoot, StockBaselineManifest target,
        IReadOnlyList<ReleaseRecipeSourceTree> baselines, string outputRoot,
        IReadOnlySet<string>? canonicalPaths = null, CancellationToken ct = default)
        => Publish(targetRoot, target, baselines, outputRoot, canonicalPaths, PACK_PROFILE, ct);

    /// <summary>
    /// Publish exact target files using the hosted release pack profile.
    /// </summary>
    /// <param name="targetRoot">The stable complete target tree.</param>
    /// <param name="target">The frozen exact target contract.</param>
    /// <param name="baselines">The independently described stock variants.</param>
    /// <param name="outputRoot">An absent output directory with an existing parent.</param>
    /// <param name="ct">The publication cancellation token.</param>
    /// <returns>The local artifact and target digests and byte measurements.</returns>
    public ReleaseRecipePublication PublishHosted(string targetRoot, StockBaselineManifest target,
        IReadOnlyList<ReleaseRecipeSourceTree> baselines, string outputRoot, CancellationToken ct = default)
        => Publish(targetRoot, target, baselines, outputRoot, null, HOSTED_PACK_PROFILE, ct);

    private ReleaseRecipePublication Publish(string targetRoot, StockBaselineManifest target,
        IReadOnlyList<ReleaseRecipeSourceTree> baselines, string outputRoot,
        IReadOnlySet<string>? canonicalPaths, string packProfile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        targetRoot = Root(targetRoot);
        outputRoot = Root(outputRoot);
        if (baselines.Count is < 1 or > 16)
            throw new InvalidDataException("A release requires one to sixteen source variants.");
        var inputRoots = new List<string> { targetRoot };
        foreach (var baseline in baselines)
        {
            inputRoots.Add(Root(baseline.Root));
            ValidateTree(baseline.Root, baseline.Manifest, ct);
        }
        RequireSeparate(outputRoot, inputRoots);
        var exactCopiesByVariant = packProfile == HOSTED_PACK_PROFILE
            ? baselines.Select(baseline => FindExactCopies(target, baseline, canonicalPaths)).ToArray()
            : null;
        var copiedTargetPaths = exactCopiesByVariant?.SelectMany(copies => copies.Keys).ToHashSet(StringComparer.Ordinal);
        // Hosted generation hashes its own inputs against the frozen target before writing recipe data.
        // Whole-file copies bypass generation, so their target bytes still need the up-front check.
        ValidateTree(targetRoot, target, ct, copiedTargetPaths);
        var targetPaths = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        if (canonicalPaths is not null && canonicalPaths.Any(path => !targetPaths.Contains(path)))
            throw new InvalidDataException("A canonical target path is absent from the complete target contract.");
        foreach (var baseline in baselines)
        {
            if (baseline.RangeHints is not null && baseline.RangeHints.Keys.Any(path => !targetPaths.Contains(path)))
                throw new InvalidDataException("A range-hint target is absent from the complete target contract.");
        }
        RequireAbsent(outputRoot);
        _fileSystem.Directory.CreateDirectory(outputRoot);
        int version = packProfile == HOSTED_PACK_PROFILE ? COMPACT_METADATA_VERSION : 1;
        var manifest = new ReleaseRecipeManifest(version, packProfile, target.ToJson(), [], [], []);
        using var packWriter = new PackWriter(this, outputRoot, manifest);
        var chunkIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        long reused = 0;
        long logical = 0;
        long unique = 0;
        long deduplicated = 0;
        long metadataBytes = manifest.TargetManifest.Length * 2L;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int variantIndex = 0; variantIndex < baselines.Count; variantIndex++)
        {
            var baseline = baselines[variantIndex];
            ct.ThrowIfCancellationRequested();
            byte[] sourceContract = baseline.Manifest.ToJson();
            if (!identities.Add(Digest(sourceContract)))
                throw new InvalidDataException("Duplicate source baseline contract.");
            Dictionary<string, string>? exactCopies = exactCopiesByVariant?[variantIndex];
            var variant = new ReleaseRecipeVariant(sourceContract, baseline.Sources.ToList(), [])
            {
                ExactCopies = exactCopies is { Count: > 0 } ? exactCopies : null,
            };
            if (variant.ExactCopies is not null)
            {
                using var copyMetadata = new BoundedMetadataStream();
                JsonSerializer.Serialize(copyMetadata, variant.ExactCopies, JSON_OPTIONS);
                metadataBytes = checked(metadataBytes + copyMetadata.Length);
                if (metadataBytes > MAX_MANIFEST_BYTES)
                    throw new InvalidDataException("Release recipe metadata budget exhausted.");
            }
            metadataBytes = checked(metadataBytes + sourceContract.Length * 2L);
            manifest.Variants.Add(variant);
            ValidateSources(variant, baseline.Manifest);
            var sources = new List<Stream>();
            try
            {
                var descriptors = new List<BundleRecipeSource>();
                foreach (var binding in baseline.Sources)
                {
                    Stream stream = OpenRead(Child(baseline.Root, binding.Path));
                    sources.Add(stream);
                    if (binding.Kind == BundleRecipeSourceKind.UnityFsData)
                        BundleRecipe.ValidateReleaseInputBounds(stream);
                    var descriptor = BundleRecipe.DescribeSource(stream, binding.Kind);
                    var expected = baseline.Manifest.Files.Single(file => file.Path == binding.Path);
                    if (descriptor.Length != expected.Length || !EqualHash(descriptor.Sha256, expected.Sha256))
                        throw new InvalidDataException("Recipe source differs from its baseline contract.");
                    descriptors.Add(descriptor);
                }
                using var matching = new BundleRecipe.ReleaseSourceIndex(descriptors, sources, GenerationBounds(), ct);
                foreach (var file in target.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (variant.ExactCopies?.ContainsKey(file.Path) == true)
                    {
                        reused = checked(reused + file.Length);
                        continue;
                    }
                    using Stream input = OpenRead(Child(targetRoot, file.Path));
                    string temporary = Child(outputRoot, "literal.tmp");
                    string canonical = Child(outputRoot, "canonical.tmp");
                    using (Stream literals = _fileSystem.File.Open(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        IReadOnlyList<BundleRecipeRangeHint>? hints = null;
                        baseline.RangeHints?.TryGetValue(file.Path, out hints);
                        BundleRecipeGenerationResult generated;
                        if (canonicalPaths?.Contains(file.Path) == true)
                        {
                            BundleRecipe.ValidateReleaseInputBounds(input);
                            using Stream normalized = _fileSystem.File.Open(canonical, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                            generated = matching.Generate(input, literals, normalized, hints, (file.Length, file.Sha256));
                        }
                        else
                        {
                            generated = matching.Generate(input, literals, null, hints, (file.Length, file.Sha256));
                        }
                        if (generated.Recipe.TargetLength != file.Length || !EqualHash(generated.Recipe.TargetSha256, file.Sha256))
                            throw new InvalidDataException("Canonical target differs from the frozen target contract. Canonicalize before freezing.");
                        reused = checked(reused + generated.Metrics.ReusedBytes);
                        logical = checked(logical + generated.Metrics.LiteralBytes);
                        var payload = new List<int>();
                        foreach (var literal in generated.LiteralChunks)
                        {
                            ct.ThrowIfCancellationRequested();
                            literals.Position = literal.Offset;
                            byte[] bytes = ReadExactly(literals, literal.Length);
                            string hash = Digest(bytes);
                            if (!EqualHash(hash, literal.Sha256))
                                throw new InvalidDataException("Generated literal chunk changed.");
                            if (chunkIndex.TryGetValue(hash, out int previous))
                            {
                                if (!packWriter.Read(previous).AsSpan().SequenceEqual(bytes))
                                    throw new InvalidDataException("Shared chunk hash collision.");
                                deduplicated += bytes.Length;
                                payload.Add(previous);
                                continue;
                            }
                            if (manifest.Chunks.Count >= MAX_RELEASE_CHUNKS)
                                throw new InvalidDataException("Release chunk record budget exhausted.");
                            int index = manifest.Chunks.Count;
                            packWriter.Store(bytes, hash);
                            chunkIndex.Add(hash, index);
                            payload.Add(index);
                            unique += bytes.Length;
                        }
                        var fileRecipe = new ReleaseFileRecipe(file.Path, generated.Recipe, payload);
                        using var fileMetadata = new BoundedMetadataStream();
                        JsonSerializer.Serialize(fileMetadata, fileRecipe, JSON_OPTIONS);
                        metadataBytes = checked(metadataBytes + fileMetadata.Length);
                        if (metadataBytes > MAX_MANIFEST_BYTES)
                            throw new InvalidDataException("Release recipe metadata budget exhausted.");
                        variant.Files.Add(fileRecipe);
                    }
                    _fileSystem.File.Delete(temporary);
                    if (_fileSystem.File.Exists(canonical))
                        _fileSystem.File.Delete(canonical);
                }
                matching.Verify();
                if (packProfile == HOSTED_PACK_PROFILE)
                    packWriter.ClosePack();
            }
            finally
            {
                foreach (Stream source in sources)
                {
                    source.Dispose();
                }
            }
        }
        if (manifest.Variants.Any(variant => variant.ExactCopies is not null))
            manifest = manifest with { Version = EXACT_COPY_VERSION };
        packWriter.Dispose();
        for (int index = 0; index < manifest.Packs.Count; index++)
        {
            using Stream pack = OpenRead(PackPath(outputRoot, index));
            manifest.Packs[index] = new ReleaseRecipePack(checked((int)pack.Length), HashStream(pack, ct));
        }
        ValidateManifest(manifest, target);
        using var json = new BoundedMetadataStream();
        WriteManifest(json, manifest);
        string artifactHash = Convert.ToHexString(SHA256.HashData(json.GetBuffer().AsSpan(0, checked((int)json.Length))));
        ct.ThrowIfCancellationRequested();
        // The marker is written last. Publication is local and deliberately does not imply signing.
        using (Stream marker = _fileSystem.File.Open(Child(outputRoot, "release.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            json.WriteTo(marker);
            marker.Flush();
        }
        return new ReleaseRecipePublication(artifactHash, Digest(manifest.TargetManifest), reused, logical,
            unique, manifest.Packs.Sum(pack => (long)pack.Length), deduplicated);
    }

    private static Dictionary<string, string> FindExactCopies(StockBaselineManifest target,
        ReleaseRecipeSourceTree baseline, IReadOnlySet<string>? canonicalPaths)
    {
        var byPath = baseline.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var byContent = new Dictionary<(long Length, string Sha256), string>();
        foreach (var file in baseline.Manifest.Files)
        {
            byContent.TryAdd((file.Length, file.Sha256), file.Path);
        }

        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in target.Files)
        {
            if (canonicalPaths?.Contains(file.Path) == true || baseline.RangeHints?.ContainsKey(file.Path) == true)
                continue;
            if (byPath.TryGetValue(file.Path, out var samePath) &&
                samePath.Length == file.Length && EqualHash(samePath.Sha256, file.Sha256))
            {
                copies.Add(file.Path, samePath.Path);
            }
            else if (byContent.TryGetValue((file.Length, file.Sha256), out string? renamedPath))
            {
                copies.Add(file.Path, renamedPath);
            }
        }
        return copies;
    }

    /// <summary>
    /// Reconstruct the complete exact target into a separate new filesystem stage.
    /// </summary>
    /// <remarks>
    /// The independent caller must bind artifact digest, target digest and source contract to its
    /// authenticated release identity. Passing locally computed digests only provides local integrity.
    /// All packs and the full baseline are verified before creating the stage. Failed owned stages
    /// are removed. Cleanup errors are attached to the original exception. No install is changed.
    /// </remarks>
    /// <param name="artifactRoot">The downloaded or locally published artifact directory.</param>
    /// <param name="expectedArtifactSha256">The independently expected release.json digest.</param>
    /// <param name="expectedTargetSha256">The independently expected complete target contract digest.</param>
    /// <param name="sourceRoot">The isolated verified baseline source root.</param>
    /// <param name="sourceContract">The independently expected stock source contract.</param>
    /// <param name="stageRoot">An absent stage directory with an existing parent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The verified complete target manifest for the launcher's preparation contract.</returns>
    public StockBaselineManifest Prepare(string artifactRoot, string expectedArtifactSha256,
        string expectedTargetSha256, string sourceRoot, StockBaselineManifest sourceContract,
        string stageRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        artifactRoot = Root(artifactRoot);
        return Prepare(Child(artifactRoot, "release.json"), expectedArtifactSha256, expectedTargetSha256,
            sourceRoot, sourceContract, stageRoot, artifactRoot, null, false, ct);
    }

    /// <summary>
    /// Reconstruct a target directly from verified download cache files.
    /// </summary>
    /// <param name="releaseFile">The canonical path to the downloaded release manifest.</param>
    /// <param name="expectedArtifactSha256">The expected manifest digest from the release feed.</param>
    /// <param name="expectedTargetSha256">The expected target contract digest from the release feed.</param>
    /// <param name="packFiles">The pack paths in manifest order. Unneeded packs may be null.</param>
    /// <param name="sourceRoot">The isolated baseline source root.</param>
    /// <param name="sourceContract">The independently expected stock source contract.</param>
    /// <param name="stageRoot">An absent stage directory with an existing parent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="retainedTargetPaths">Target paths independently verified and retained by the caller, if any.</param>
    /// <returns>The verified complete target manifest.</returns>
    public StockBaselineManifest PrepareDownloaded(string releaseFile, string expectedArtifactSha256,
        string expectedTargetSha256, IReadOnlyList<string?> packFiles, string sourceRoot, StockBaselineManifest sourceContract,
        string stageRoot, CancellationToken ct = default, IReadOnlySet<string>? retainedTargetPaths = null)
    {
        ct.ThrowIfCancellationRequested();
        return Prepare(Root(releaseFile), expectedArtifactSha256, expectedTargetSha256, sourceRoot, sourceContract,
            stageRoot, null, packFiles.ToArray(), false, ct, retainedTargetPaths: retainedTargetPaths);
    }

    /// <summary>
    /// Reconstruct a target from verified downloads and an isolated projection of a verified stock archive.
    /// </summary>
    /// <param name="releaseFile">The canonical path to the downloaded release manifest.</param>
    /// <param name="expectedArtifactSha256">The expected manifest digest from the release feed.</param>
    /// <param name="expectedTargetSha256">The expected target contract digest from the release feed.</param>
    /// <param name="packFiles">The pack paths in manifest order. Unneeded packs may be null.</param>
    /// <param name="sourceRoot">The isolated recipe inputs extracted after verifying complete stock coverage.</param>
    /// <param name="sourceContract">The independently expected complete stock contract used to select the recipe.</param>
    /// <param name="stageRoot">An absent stage directory with an existing parent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="retainedTargetPaths">Target paths independently verified and retained by the caller, if any.</param>
    /// <returns>The verified complete target manifest.</returns>
    public StockBaselineManifest PrepareDownloadedFromRecipeInputs(string releaseFile, string expectedArtifactSha256,
        string expectedTargetSha256, IReadOnlyList<string?> packFiles, string sourceRoot, StockBaselineManifest sourceContract,
        string stageRoot, CancellationToken ct = default, IReadOnlySet<string>? retainedTargetPaths = null)
    {
        ct.ThrowIfCancellationRequested();
        return Prepare(Root(releaseFile), expectedArtifactSha256, expectedTargetSha256, sourceRoot, sourceContract,
            stageRoot, null, packFiles.ToArray(), false, ct, recipeInputsOnly: true, retainedTargetPaths: retainedTargetPaths);
    }

    /// <summary>
    /// Reconstruct only new or changed paths for an update against the exact installed generation.
    /// </summary>
    /// <remarks>
    /// The complete source is still verified. The caller must keep the unchanged target paths bound to
    /// the installed generation through application and independently verify the final installed tree.
    /// </remarks>
    /// <param name="releaseFile">The canonical path to the downloaded release manifest.</param>
    /// <param name="expectedArtifactSha256">The expected manifest digest from the release feed.</param>
    /// <param name="expectedTargetSha256">The expected target contract digest from the release feed.</param>
    /// <param name="packFiles">The pack paths in manifest order. Unneeded packs may be null.</param>
    /// <param name="sourceRoot">The installed source generation.</param>
    /// <param name="sourceContract">The independently expected complete installed source contract.</param>
    /// <param name="stageRoot">An absent stage directory with an existing parent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The complete target manifest, including unchanged paths supplied by the installed generation.</returns>
    public StockBaselineManifest PrepareDownloadedUpdate(string releaseFile, string expectedArtifactSha256,
        string expectedTargetSha256, IReadOnlyList<string?> packFiles, string sourceRoot, StockBaselineManifest sourceContract,
        string stageRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Prepare(Root(releaseFile), expectedArtifactSha256, expectedTargetSha256, sourceRoot, sourceContract,
            stageRoot, null, packFiles.ToArray(), true, ct);
    }

    /// <summary>
    /// Get the stage contract for an update. Omit only files identical at the same installed path.
    /// </summary>
    /// <param name="target">The complete expected release contract.</param>
    /// <param name="installed">The complete expected installed source contract.</param>
    /// <returns>The target files requiring staging and the target directory layout.</returns>
    public static StockBaselineManifest UpdateStageContract(StockBaselineManifest target, StockBaselineManifest installed)
    {
        var installedFiles = installed.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        return new StockBaselineManifest(target.BaselineIdentity, target.Provenance,
            target.Files.Where(file => installedFiles.GetValueOrDefault(file.Path) != file),
            target.Directories, target.HasUnixMetadata ? target.UnixDirectoryModes : null);
    }

    /// <summary>
    /// Get the stage contract when the installation retains independently verified target paths.
    /// </summary>
    /// <param name="target">The complete authenticated target.</param>
    /// <param name="retainedTargetPaths">The exact ordinal paths already supplied by the installation.</param>
    /// <returns>The files requiring staging and the target directory layout.</returns>
    public static StockBaselineManifest RetainedStageContract(StockBaselineManifest target, IReadOnlySet<string> retainedTargetPaths)
    {
        var known = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        if (!retainedTargetPaths.All(known.Contains))
            throw new InvalidDataException("Retention includes a path outside the authenticated target.");
        return new StockBaselineManifest(target.BaselineIdentity, target.Provenance,
            target.Files.Where(file => !retainedTargetPaths.Contains(file.Path)), target.Directories,
            target.HasUnixMetadata ? target.UnixDirectoryModes : null);
    }

    private StockBaselineManifest Prepare(string releaseFile, string expectedArtifactSha256,
        string expectedTargetSha256, string sourceRoot, StockBaselineManifest sourceContract, string stageRoot,
        string? artifactRoot, string?[]? packFiles, bool stageChangesOnly, CancellationToken ct, bool recipeInputsOnly = false,
        IReadOnlySet<string>? retainedTargetPaths = null)
    {
        sourceRoot = Root(sourceRoot);
        stageRoot = Root(stageRoot);
        RequireSeparate(stageRoot, [artifactRoot ?? releaseFile, sourceRoot]);
        RequireAbsent(stageRoot);
        var manifest = ReadManifest(releaseFile, expectedArtifactSha256, expectedTargetSha256, ct, out var target);
        var selected = SelectVariant(manifest, sourceContract);
        var staged = stageChangesOnly ? UpdateStageContract(target, sourceContract)
            : retainedTargetPaths is null ? target : RetainedStageContract(target, retainedTargetPaths);
        if (packFiles is not null)
        {
            if (packFiles.Length != manifest.Packs.Count || RequiredPacks(manifest, selected, retainedTargetPaths).Any(index => packFiles[index] is null))
                throw new InvalidDataException("Downloaded pack paths do not cover the verified manifest.");
            RequireSeparate(stageRoot, packFiles.OfType<string>());
        }
        string GetPackPath(int index) => packFiles is null ? PackPath(artifactRoot!, index) :
            packFiles[index] ?? throw new InvalidDataException("Required release pack was not downloaded.");
        ValidateTree(sourceRoot, recipeInputsOnly ? RecipeInputContract(selected, sourceContract, retainedTargetPaths) : sourceContract, ct);
        for (int index = 0; index < manifest.Packs.Count; index++)
        {
            if (packFiles is not null && packFiles[index] is null)
                continue;
            using Stream pack = OpenRead(GetPackPath(index));
            Verify(pack, manifest.Packs[index].Length, manifest.Packs[index].Sha256, ct);
        }
        var sources = new List<Stream>();
        bool stageCreated = false;
        try
        {
            foreach (var binding in selected.Sources)
            {
                Stream input = OpenRead(Child(sourceRoot, binding.Path));
                sources.Add(input);
                if (binding.Kind == BundleRecipeSourceKind.UnityFsData)
                    BundleRecipe.ValidateReleaseInputBounds(input);
            }
            ct.ThrowIfCancellationRequested();
            StockArchive.CreateDirectory(_fileSystem, stageRoot);
            stageCreated = true;
            foreach (string directory in staged.Directories)
            {
                ct.ThrowIfCancellationRequested();
                StockArchive.CreateDirectory(_fileSystem, Child(stageRoot, directory));
                if (target.HasUnixMetadata)
                    StockArchive.ApplyUnixPermissions(_fileSystem, Child(stageRoot, directory), target.UnixDirectoryModes[directory]);
                StockArchive.FlushLinuxDirectory(_fileSystem, _fileSystem.Path.GetDirectoryName(Child(stageRoot, directory))!);
            }
            using var packReader = new PackReader(this, manifest, GetPackPath);
            var recipes = selected.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            foreach (var expected in staged.Files)
            {
                ct.ThrowIfCancellationRequested();
                string destination = Child(stageRoot, expected.Path);
                using Stream output = _fileSystem.File.Open(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                if (selected.ExactCopies?.TryGetValue(expected.Path, out string? sourcePath) == true)
                {
                    using Stream input = OpenRead(Child(sourceRoot, sourcePath));
                    CopyExactFile(input, output, expected.Length, ct);
                }
                else
                {
                    var file = recipes[expected.Path];
                    using var payload = new ReleasePayloadStream(packReader, manifest, file.PayloadChunks, ct);
                    file.Recipe.Reconstruct(sources, payload, output);
                }
                output.Flush();
                if (expected.UnixMode is not null)
                    StockArchive.ApplyUnixPermissions(_fileSystem, destination, expected.UnixMode.Value);
                StockArchive.FlushLinuxDirectory(_fileSystem, _fileSystem.Path.GetDirectoryName(destination)!);
            }
            ValidateTree(stageRoot, staged, ct);
            ct.ThrowIfCancellationRequested();
            return target;
        }
        catch (Exception error)
        {
            if (stageCreated)
            {
                try
                {
                    _pathValidator.ValidateControlledPath(stageRoot);
                    _fileSystem.Directory.Delete(stageRoot, true);
                }
                catch (Exception cleanup)
                {
                    error.Data["ReleasePreparationCleanupFailure"] = cleanup;
                    error.Data["ReleasePreparationStage"] = stageRoot;
                }
            }
            throw;
        }
        finally
        {
            foreach (Stream source in sources)
            {
                source.Dispose();
            }
        }
    }

    private static ReleaseRecipeVariant SelectVariant(ReleaseRecipeManifest manifest, StockBaselineManifest sourceContract)
    {
        byte[] sourceBytes = sourceContract.ToJson();
        return manifest.Variants.SingleOrDefault(variant => variant.SourceManifest.AsSpan().SequenceEqual(sourceBytes))
            ?? throw new InvalidDataException("No recipe variant supports the independently expected baseline.");
    }

    private static StockBaselineManifest RecipeInputContract(ReleaseRecipeVariant variant, StockBaselineManifest sourceContract,
        IReadOnlySet<string>? retainedTargetPaths)
    {
        var paths = variant.Sources.Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
        if (variant.ExactCopies is { } exactCopies)
            paths.UnionWith(exactCopies.Where(copy => retainedTargetPaths?.Contains(copy.Key) != true).Select(copy => copy.Value));
        return new StockBaselineManifest(sourceContract.BaselineIdentity, sourceContract.Provenance,
            sourceContract.Files.Where(file => paths.Contains(file.Path)), sourceContract.Directories,
            sourceContract.HasUnixMetadata ? sourceContract.UnixDirectoryModes : null);
    }

    private static HashSet<int> RequiredPacks(ReleaseRecipeManifest manifest, ReleaseRecipeVariant variant,
        IReadOnlySet<string>? retainedTargetPaths)
    {
        var indices = new HashSet<int>();
        foreach (var file in variant.Files)
        {
            if (retainedTargetPaths?.Contains(file.Path) == true)
                continue;
            foreach (int chunk in file.PayloadChunks)
            {
                indices.Add(manifest.Chunks[chunk].PackIndex);
            }
        }
        return indices;
    }

    private static void CopyExactFile(Stream input, Stream output, long length, CancellationToken ct)
    {
        if (input.Length != length)
            throw new InvalidDataException("Exact copy source length changed during release preparation.");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MAX_CHUNK_BYTES);
        try
        {
            while (length > 0)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(length, MAX_CHUNK_BYTES);
                input.ReadExactly(buffer.AsSpan(0, count));
                output.Write(buffer, 0, count);
                length -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static BundleRecipeGenerationOptions GenerationBounds() => new()
    {
        MaxIndexedChunks = MAX_FILE_RECORDS,
        MaxRecipeSegments = MAX_FILE_RECORDS,
    };

    private static (int Size, int Count) PackLimits(string profile) => profile switch
    {
        PACK_PROFILE => (MAX_PACK_BYTES, MAX_FILE_RECORDS),
        HOSTED_PACK_PROFILE => (MAX_HOSTED_PACK_BYTES, MAX_HOSTED_PACKS),
        _ => throw new InvalidDataException("Unsupported release pack profile."),
    };

    private sealed class PackWriter(ReleaseRecipePipeline owner, string root, ReleaseRecipeManifest manifest) : IDisposable
    {
        private readonly (int Size, int Count) _limits = PackLimits(manifest.PackProfile);
        private Stream? _current;

        public void Store(byte[] bytes, string hash)
        {
            byte[] compressed = LZ4Codec.Encode32HC(bytes, 0, bytes.Length);
            bool isCompressed = compressed.Length < bytes.Length;
            byte[] stored = isCompressed ? compressed : bytes;
            int index = manifest.Packs.Count - 1;
            if (_current is null || manifest.Packs[index].Length > _limits.Size - stored.Length)
            {
                ClosePack();
                index++;
                if (index >= _limits.Count)
                    throw new InvalidDataException("Release pack count exceeds its profile budget.");
                _current = owner._fileSystem.File.Open(owner.PackPath(root, index), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None);
                manifest.Packs.Add(new ReleaseRecipePack(0, new string('0', 64)));
            }
            var pack = manifest.Packs[index];
            _current.Position = pack.Length;
            _current.Write(stored);
            manifest.Chunks.Add(new ReleaseRecipeChunk(hash, bytes.Length, index, pack.Length, stored.Length, isCompressed));
            manifest.Packs[index] = pack with { Length = checked(pack.Length + stored.Length) };
        }

        public byte[] Read(int index)
        {
            var chunk = manifest.Chunks[index];
            return _current is not null && chunk.PackIndex == manifest.Packs.Count - 1
                ? ReadChunk(_current, chunk)
                : owner.ReadChunk(root, manifest, index);
        }

        public void Dispose() => ClosePack();

        public void ClosePack()
        {
            _current?.Dispose();
            _current = null;
        }
    }

    private byte[] ReadChunk(string root, ReleaseRecipeManifest manifest, int index)
    {
        var chunk = manifest.Chunks[index];
        using Stream pack = OpenRead(PackPath(root, chunk.PackIndex));
        return ReadChunk(pack, chunk);
    }

    private static byte[] ReadChunk(Stream pack, ReleaseRecipeChunk chunk)
    {
        pack.Position = chunk.Offset;
        byte[] stored = ReadExactly(pack, chunk.StoredLength);
        byte[] bytes;
        try
        {
            bytes = chunk.IsCompressed ? LZ4Codec.Decode32(stored, 0, stored.Length, chunk.Length) : stored;
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Corrupt release literal compression.", error);
        }
        if (bytes.Length != chunk.Length || Digest(bytes) != chunk.Sha256)
            throw new InvalidDataException("Release literal SHA-256 mismatch.");
        return bytes;
    }

    private void ValidateTree(string root, StockBaselineManifest contract, CancellationToken ct,
        IReadOnlySet<string>? hashPaths = null)
    {
        new StockArchiveLimits().Validate(contract);
        _pathValidator.ValidateControlledPath(Root(root));
        foreach (string directory in contract.Directories)
        {
            ct.ThrowIfCancellationRequested();
            if (!_fileSystem.Directory.Exists(Child(root, directory)))
                throw new InvalidDataException("Expected release tree directory is absent.");
            if (contract.HasUnixMetadata && !StockArchive.UnixPermissionsMatch(_fileSystem, Child(root, directory), contract.UnixDirectoryModes[directory]))
                throw new InvalidDataException("Release tree directory permissions differ from its contract.");
        }
        foreach (var file in contract.Files)
        {
            ct.ThrowIfCancellationRequested();
            using Stream input = OpenRead(Child(root, file.Path));
            if (hashPaths is null || hashPaths.Contains(file.Path))
                Verify(input, file.Length, file.Sha256, ct);
            else if (input.Length != file.Length)
                throw new InvalidDataException("Release input length or SHA-256 mismatch.");
            if (file.UnixMode is not null && !StockArchive.UnixPermissionsMatch(_fileSystem, Child(root, file.Path), file.UnixMode.Value))
                throw new InvalidDataException("Release tree file permissions differ from its contract.");
        }
    }

    private static void ValidateSources(ReleaseRecipeVariant variant, StockBaselineManifest contract)
    {
        if (variant.Sources is null || variant.Sources.Count > 16)
            throw new InvalidDataException("A recipe variant supports at most sixteen matching source files.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in variant.Sources)
        {
            if (source is null || !paths.Add(source.Path) ||
                !contract.Files.Any(file => file.Path == source.Path) ||
                source.Kind is not (BundleRecipeSourceKind.RawFile or BundleRecipeSourceKind.UnityFsData))
                throw new InvalidDataException("Recipe source is absent, duplicated or has an unsupported coordinate space.");
        }
    }

    private static void ValidateManifest(ReleaseRecipeManifest manifest, StockBaselineManifest target)
    {
        new StockArchiveLimits().Validate(target);
        var limits = PackLimits(manifest.PackProfile);
        if ((manifest.Version != 1 && (manifest.Version is not (COMPACT_METADATA_VERSION or EXACT_COPY_VERSION) ||
                manifest.PackProfile != HOSTED_PACK_PROFILE)) ||
            manifest.Variants is null ||
            manifest.Variants.Count is < 1 or > 16 || manifest.Packs is null || manifest.Chunks is null ||
            manifest.Chunks.Count > MAX_RELEASE_CHUNKS || manifest.Packs.Count > limits.Count)
            throw new InvalidDataException("Unsupported release format or record budget.");
        var ends = new long[manifest.Packs.Count];
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in manifest.Packs)
        {
            if (pack is null || pack.Length <= 0 || pack.Length > limits.Size || !IsHash(pack.Sha256))
                throw new InvalidDataException("Invalid release pack identity or bound.");
        }
        foreach (var chunk in manifest.Chunks)
        {
            if (chunk is null || !IsHash(chunk.Sha256) || !hashes.Add(chunk.Sha256) ||
                chunk.Length is <= 0 or > MAX_CHUNK_BYTES || chunk.StoredLength <= 0 ||
                chunk.StoredLength > chunk.Length || (chunk.IsCompressed && chunk.StoredLength >= chunk.Length) ||
                (!chunk.IsCompressed && chunk.StoredLength != chunk.Length) ||
                chunk.PackIndex < 0 || chunk.PackIndex >= ends.Length || chunk.Offset != ends[chunk.PackIndex])
                throw new InvalidDataException("Invalid, overlapping or oversized release chunk.");
            ends[chunk.PackIndex] += chunk.StoredLength;
            if (ends[chunk.PackIndex] > manifest.Packs[chunk.PackIndex].Length)
                throw new InvalidDataException("Release chunk exceeds its pack.");
        }
        if (ends.Where((length, index) => length != manifest.Packs[index].Length).Any())
            throw new InvalidDataException("Release chunks do not completely fill their packs.");
        var sourceContracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in manifest.Variants)
        {
            if (variant is null || variant.SourceManifest is null || !sourceContracts.Add(Digest(variant.SourceManifest)))
                throw new InvalidDataException("Missing or duplicate release source contract.");
            var baseline = StockBaselineManifest.FromJson(variant.SourceManifest);
            ValidateSources(variant, baseline);
            var exactCopies = variant.ExactCopies;
            if (exactCopies is not null && manifest.Version != EXACT_COPY_VERSION)
                throw new InvalidDataException("Exact copies require release format three.");
            var baselineFiles = baseline.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            if (variant.Files is null || variant.Files.Count + (long)(exactCopies?.Count ?? 0) != target.Files.Count)
                throw new InvalidDataException("Recipe set does not cover the complete target.");
            int recipeIndex = 0;
            foreach (var expected in target.Files)
            {
                if (exactCopies?.TryGetValue(expected.Path, out string? sourcePath) == true)
                {
                    if (sourcePath is null || !baselineFiles.TryGetValue(sourcePath, out var sourceFile) ||
                        sourceFile.Length != expected.Length || !EqualHash(sourceFile.Sha256, expected.Sha256))
                        throw new InvalidDataException("Exact copy source differs from its target contract.");
                    continue;
                }
                if (recipeIndex == variant.Files.Count)
                    throw new InvalidDataException("Recipe set does not cover the complete target.");
                var file = variant.Files[recipeIndex++];
                var recipe = file?.Recipe;
                if (file is null || file.Path != expected.Path || recipe is null ||
                    recipe.TargetLength != expected.Length || !EqualHash(recipe.TargetSha256, expected.Sha256) ||
                    file.PayloadChunks is null || file.PayloadChunks.Count > MAX_FILE_RECORDS ||
                    recipe.Sources is null || recipe.Sources.Count != variant.Sources.Count ||
                    recipe.Entries is null || recipe.Entries.Count > 65536 ||
                    recipe.Entries.Any(entry => entry is null || entry.Segments is null) ||
                    recipe.Entries.Sum(entry => (long)entry.Segments.Count) > MAX_FILE_RECORDS)
                    throw new InvalidDataException("Invalid target recipe identity or metadata bound.");
                long payloadLength = 0;
                foreach (int chunk in file.PayloadChunks)
                {
                    if (chunk < 0 || chunk >= manifest.Chunks.Count)
                        throw new InvalidDataException("Recipe references an absent literal chunk.");
                    payloadLength = checked(payloadLength + manifest.Chunks[chunk].Length);
                }
                if (payloadLength != recipe.PayloadLength)
                    throw new InvalidDataException("Recipe virtual literal length mismatch.");
                recipe.ValidateStructure();
                if (recipe.Version == BundleRecipe.CURRENT_VERSION && recipe.Entries.Sum(entry => entry.Length) > 8L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Canonical target data exceeds the eight GiB preparation bound.");
                for (int sourceIndex = 0; sourceIndex < recipe.Sources.Count; sourceIndex++)
                {
                    var binding = variant.Sources[sourceIndex];
                    var source = recipe.Sources[sourceIndex];
                    var identity = baselineFiles[binding.Path];
                    if (source is null || source.Kind != binding.Kind || source.Length != identity.Length ||
                        !EqualHash(source.Sha256, identity.Sha256))
                        throw new InvalidDataException("Recipe source identity differs from its baseline contract.");
                }
            }
        }
    }

    private string Root(string path)
    {
        string full = _fileSystem.Path.TrimEndingDirectorySeparator(_fileSystem.Path.GetFullPath(path));
        if (full != path || path.Replace('\\', '/').Split('/').Contains(".."))
            throw new InvalidDataException("Release roots must be canonical absolute paths.");
        _pathValidator.ValidateControlledPath(full);
        return full;
    }

    private string Child(string root, string relative)
    {
        string normalized = StockBaselineManifest.NormalizePath(relative);
        string full = _fileSystem.Path.Combine(root, normalized.Replace('/', _fileSystem.Path.DirectorySeparatorChar));
        _pathValidator.ValidateControlledPath(full);
        return full;
    }

    private string PackPath(string root, int index) => Child(root, $"pack-{index:D6}.bin");

    private void RequireAbsent(string root)
    {
        if (!_fileSystem.Directory.Exists(_fileSystem.Path.GetDirectoryName(root)!) ||
            _fileSystem.File.Exists(root) || _fileSystem.Directory.Exists(root))
            throw new IOException("Release output must be absent and have an existing parent.");
    }

    private void RequireSeparate(string root, IEnumerable<string> inputs)
    {
        foreach (string input in inputs)
        {
            string other = Root(input);
            string separator = _fileSystem.Path.DirectorySeparatorChar.ToString();
            if (root.Equals(other, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(other + separator, StringComparison.OrdinalIgnoreCase) ||
                other.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release output and input roots must be separate.");
        }
    }

    private Stream OpenRead(string path)
    {
        _pathValidator.ValidateControlledPath(path);
        return _fileSystem.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        if (length < 0 || length > MAX_CHUNK_BYTES)
            throw new InvalidDataException("Release read exceeds its bounded buffer.");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void Verify(Stream stream, long length, string hash, CancellationToken ct)
    {
        if (stream.Length != length || !IsHash(hash) || !EqualHash(HashStream(stream, ct), hash))
            throw new InvalidDataException("Release input length or SHA-256 mismatch.");
        stream.Position = 0;
    }

    private static string HashStream(Stream stream, CancellationToken ct)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MAX_CHUNK_BYTES];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static bool EqualHash(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private sealed class BoundedMetadataStream : MemoryStream
    {
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position > MAX_MANIFEST_BYTES - count)
                throw new InvalidDataException("Release metadata exceeds its byte budget.");
            base.Write(buffer, offset, count);
        }

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position > MAX_MANIFEST_BYTES - buffer.Length)
                throw new InvalidDataException("Release metadata exceeds its byte budget.");
            base.Write(buffer);
        }
    }
}
