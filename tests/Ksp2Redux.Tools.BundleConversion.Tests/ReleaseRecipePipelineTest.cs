using System.IO.Abstractions;
using System.Buffers.Binary;
using AssetsTools.NET;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Standalone behavioral verification of release deduplication, exact staging and rejected inputs.
/// </summary>
public class ReleaseRecipePipelineTest
{
    private MockFileSystem _fileSystem = null!;
    private ReleaseRecipePipeline _pipeline = null!;
    private const string ROOT = @"C:\ReleaseTest";
    private const string TARGET = ROOT + @"\target";
    private const string BASELINE = ROOT + @"\stock";
    private const string ARTIFACT = ROOT + @"\artifact";
    private const string STAGE = ROOT + @"\stage";

    /// <summary>
    /// Create an isolated Windows filesystem before each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        _fileSystem.Directory.CreateDirectory(TARGET);
        _fileSystem.Directory.CreateDirectory(BASELINE);
        _pipeline = new ReleaseRecipePipeline(_fileSystem, _ => { });
    }

    /// <summary>
    /// Deduplicate literals across files and reconstruct source ranges, empty files and empty directories.
    /// </summary>
    [Test]
    public void SharedLiteralsAndSourceRangesReconstructCompleteTree()
    {
        byte[] old = RandomBytes(800000);
        byte[] literal = Enumerable.Repeat((byte)81, 270000).ToArray();
        var baseline = WriteContract(BASELINE, ("old.bin", old));
        var target = WriteContract(TARGET, ("a.bin", old.Concat(literal).ToArray()),
            ("nested/b.bin", literal), ("empty", []));
        _fileSystem.Directory.CreateDirectory(TARGET + @"\empty-dir");
        target = new StockBaselineManifest(target.BaselineIdentity, target.Provenance, target.Files,
            target.Directories.Concat(["empty-dir"]));

        var published = Publish(target, baseline);
        var prepared = Prepare(published, baseline);

        Assert.That(prepared.ToJson(), Is.EqualTo(target.ToJson()));
        foreach (var file in target.Files)
        {
            Assert.That(_fileSystem.File.ReadAllBytes(STAGE + "\\" + file.Path.Replace('/', '\\')),
                Is.EqualTo(_fileSystem.File.ReadAllBytes(TARGET + "\\" + file.Path.Replace('/', '\\'))));
        }
        Assert.That(_fileSystem.Directory.Exists(STAGE + @"\empty-dir"), Is.True);
        Assert.That(published.SourceReusedBytes, Is.GreaterThan(700000));
        Assert.That(published.CrossRecipeDeduplicatedBytes, Is.GreaterThan(0));
        Assert.That(published.StoredPackBytes, Is.LessThan(published.UniqueLiteralBytes));
        Assert.That(_fileSystem.File.ReadAllBytes(BASELINE + @"\old.bin"), Is.EqualTo(old));
    }

    /// <summary>
    /// Store incompressible chunks raw, split packs at their bound and stage across pack boundaries.
    /// </summary>
    [Test]
    public void BoundedPacksSelectRawAndReconstructAcrossBoundaries()
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 17 }));
        byte[] bytes = RandomBytes(9 * 1024 * 1024 + 63);
        var target = WriteContract(TARGET, ("a.bin", bytes), ("b.bin", bytes));

        var result = Publish(target, baseline);
        var manifest = ReadManifest();
        Prepare(result, baseline);

        Assert.That(manifest.Packs, Has.Count.EqualTo(2));
        Assert.That(manifest.Packs.All(pack => pack.Length <= ReleaseRecipePipeline.MAX_PACK_BYTES), Is.True);
        Assert.That(manifest.Chunks.All(chunk => chunk.Length <= ReleaseRecipePipeline.MAX_CHUNK_BYTES), Is.True);
        Assert.That(manifest.Chunks.Any(chunk => !chunk.IsCompressed), Is.True);
        Assert.That(result.CrossRecipeDeduplicatedBytes, Is.EqualTo(result.UniqueLiteralBytes));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\b.bin"), Is.EqualTo(bytes));
    }

    /// <summary>
    /// Publish deterministic bytes when inputs and variant order are unchanged.
    /// </summary>
    [Test]
    public void RepeatedPublicationHasIdenticalManifestAndPacks()
    {
        var baseline = WriteContract(BASELINE, ("old.bin", RandomBytes(100000)));
        var target = WriteContract(TARGET, ("new.bin", RandomBytes(300000)));
        var first = Publish(target, baseline);
        string other = ROOT + @"\artifact2";

        var second = _pipeline.Publish(TARGET, target, Sources(baseline), other);

        Assert.That(second, Is.EqualTo(first));
        Assert.That(_fileSystem.File.ReadAllBytes(other + @"\release.json"),
            Is.EqualTo(_fileSystem.File.ReadAllBytes(ARTIFACT + @"\release.json")));
    }

    /// <summary>
    /// Reuse whole files at their original and renamed paths without publishing any packs.
    /// </summary>
    [Test]
    public void HostedExactCopiesReconstructOriginalAndRenamedPathsWithoutPacks()
    {
        byte[] bytes = RandomBytes(250000);
        var baseline = WriteContract(BASELINE, ("copy.bin", bytes), ("other.bin", bytes));
        var target = WriteContract(TARGET, ("other.bin", bytes), ("renamed.bin", bytes));

        var result = _pipeline.PublishHosted(TARGET, target,
            [new ReleaseRecipeSourceTree(BASELINE, baseline, [])], ARTIFACT);
        var manifest = ReadManifest();
        Prepare(result, baseline);

        Assert.That(result.SourceReusedBytes, Is.EqualTo(target.TotalBytes));
        Assert.That(manifest.Packs, Is.Empty);
        Assert.That(manifest.Variants[0].Files, Is.Empty);
        Assert.That(manifest.Variants[0].ExactCopies, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["other.bin"] = "other.bin",
            ["renamed.bin"] = "copy.bin",
        }));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\renamed.bin"), Is.EqualTo(bytes));
    }

    /// <summary>
    /// Reject a copy mapping that disagrees with the independently expected file contents or format.
    /// </summary>
    /// <param name="legacyVersion">True to reject the mapping in the older metadata format.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void InvalidExactCopyMappingCannotCreateStage(bool legacyVersion)
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }), ("other.bin", new byte[] { 2 }));
        var target = WriteContract(TARGET, ("renamed.bin", new byte[] { 1 }));
        var result = _pipeline.PublishHosted(TARGET, target,
            [new ReleaseRecipeSourceTree(BASELINE, baseline, [])], ARTIFACT);
        var manifest = ReadManifest();
        if (legacyVersion)
        {
            manifest = manifest with { Version = 2 };
        }
        else
        {
            manifest.Variants[0].ExactCopies!["renamed.bin"] = "other.bin";
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        _fileSystem.File.WriteAllBytes(ARTIFACT + @"\release.json", json);
        result = result with { ArtifactSha256 = Convert.ToHexString(SHA256.HashData(json)) };

        Assert.Throws<InvalidDataException>(() => Prepare(result, baseline));
        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);
    }

    /// <summary>
    /// Downloaded preparation accepts only the packs needed by the selected baseline.
    /// </summary>
    [Test]
    public void DownloadedPreparationRequiresOnlySelectedVariantPacks()
    {
        string alternateRoot = ROOT + @"\alternate";
        byte[] bytes = RandomBytes(250000);
        var baseline = WriteContract(BASELINE, ("copy.bin", bytes));
        var alternate = WriteContract(alternateRoot, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("renamed.bin", bytes));
        var result = _pipeline.PublishHosted(TARGET, target,
            [new ReleaseRecipeSourceTree(BASELINE, baseline, []), new ReleaseRecipeSourceTree(alternateRoot, alternate, [])],
            ARTIFACT);
        var manifest = _pipeline.Inspect(ARTIFACT, result.ArtifactSha256, result.TargetManifestSha256);
        string marker = ARTIFACT + @"\release.json";
        string?[] packFiles = new string?[manifest.Packs.Count];

        Assert.That(ReleaseRecipePipeline.GetRequiredPackIndices(manifest, baseline), Is.Empty);
        Assert.That(ReleaseRecipePipeline.GetRequiredPackIndices(manifest, alternate), Is.EqualTo(new[] { 0 }));
        Assert.Throws<InvalidDataException>(() => _pipeline.PrepareDownloaded(marker, result.ArtifactSha256,
            result.TargetManifestSha256, packFiles, alternateRoot, alternate, STAGE));
        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);

        _pipeline.PrepareDownloaded(marker, result.ArtifactSha256, result.TargetManifestSha256,
            packFiles, BASELINE, baseline, STAGE);

        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\renamed.bin"), Is.EqualTo(bytes));
    }

    /// <summary>
    /// Stage renamed and changed files while leaving the identical installed paths in place.
    /// </summary>
    [Test]
    public void InstalledUpdateStagesOnlyNewOrChangedPaths()
    {
        var baseline = WriteContract(BASELINE, ("unchanged.bin", new byte[] { 1 }),
            ("old.bin", new byte[] { 2 }), ("modified.bin", new byte[] { 3 }));
        var target = WriteContract(TARGET, ("unchanged.bin", new byte[] { 1 }),
            ("nested/renamed.bin", new byte[] { 2 }), ("modified.bin", new byte[] { 4 }));
        var publication = _pipeline.PublishHosted(TARGET, target,
            [new ReleaseRecipeSourceTree(BASELINE, baseline, [])], ARTIFACT);
        var manifest = _pipeline.Inspect(ARTIFACT, publication.ArtifactSha256, publication.TargetManifestSha256);
        var packs = Enumerable.Range(0, manifest.Packs.Count)
            .Select(index => ARTIFACT + $@"\pack-{index:D6}.bin").ToArray();

        var prepared = _pipeline.PrepareDownloadedUpdate(ARTIFACT + @"\release.json",
            publication.ArtifactSha256, publication.TargetManifestSha256, packs, BASELINE, baseline, STAGE);

        Assert.That(prepared.ToJson(), Is.EqualTo(target.ToJson()));
        Assert.That(_fileSystem.File.Exists(STAGE + @"\unchanged.bin"), Is.False);
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\nested\renamed.bin"), Is.EqualTo(new byte[] { 2 }));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\modified.bin"), Is.EqualTo(new byte[] { 4 }));
        Assert.That(_fileSystem.File.ReadAllBytes(BASELINE + @"\old.bin"), Is.EqualTo(new byte[] { 2 }));
    }

    /// <summary>
    /// Reject corrupt or truncated packs before creating a target stage.
    /// </summary>
    /// <param name="truncate">True to truncate the pack, false to corrupt one byte.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void PackDamageIsRejectedBeforeStaging(bool truncate)
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("new.bin", RandomBytes(200000)));
        var result = Publish(target, baseline);
        string pack = ARTIFACT + @"\pack-000000.bin";
        byte[] bytes = _fileSystem.File.ReadAllBytes(pack);
        bytes[0] ^= 1;
        _fileSystem.File.WriteAllBytes(pack, truncate ? bytes[..^1] : bytes);

        Assert.Throws<InvalidDataException>(() => Prepare(result, baseline));

        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);
    }

    /// <summary>
    /// Independently pinned artifact and tree digests reject changed metadata.
    /// </summary>
    /// <param name="artifact">True to change the artifact digest, false to change the tree digest.</param>
    [TestCase(true)]
    [TestCase(false)]
    public void WrongIndependentDigestIsRejected(bool artifact)
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("new.bin", new byte[] { 2 }));
        var result = Publish(target, baseline);

        Assert.Throws<InvalidDataException>(() => _pipeline.Prepare(ARTIFACT,
            artifact ? new string('0', 64) : result.ArtifactSha256,
            artifact ? result.TargetManifestSha256 : new string('0', 64), BASELINE, baseline, STAGE));
        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);
    }

    /// <summary>
    /// Reject a different supported-contract selector and mismatched actual baseline bytes.
    /// </summary>
    /// <param name="contract">True to replace the expected contract, false to corrupt the actual source.</param>
    [TestCase(true)]
    [TestCase(false)]
    public void WrongBaselineIsRejected(bool contract)
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("new.bin", new byte[] { 2 }));
        var result = Publish(target, baseline);
        if (contract)
            baseline = new StockBaselineManifest("wrong", baseline.Provenance, baseline.Files);
        else
            _fileSystem.File.WriteAllBytes(BASELINE + @"\old.bin", new byte[] { 3 });

        Assert.Throws<InvalidDataException>(() => Prepare(result, baseline));
        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);
    }

    /// <summary>
    /// Baseline variants share literal storage while each selector retains its own exact recipes.
    /// </summary>
    [Test]
    public void MultipleBaselinesSelectTheirOwnCompleteRecipes()
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        string other = ROOT + @"\other-stock";
        _fileSystem.Directory.CreateDirectory(other);
        var second = WriteContract(other, ("old.bin", new byte[] { 3 }));
        var target = WriteContract(TARGET, ("new.bin", RandomBytes(200000)));
        var variants = Sources(baseline).Concat([new ReleaseRecipeSourceTree(other, second,
            [new ReleaseRecipeSource("old.bin", BundleRecipeSourceKind.RawFile)])]).ToList();

        var result = _pipeline.Publish(TARGET, target, variants, ARTIFACT);
        _pipeline.Prepare(ARTIFACT, result.ArtifactSha256, result.TargetManifestSha256, other, second, STAGE);

        Assert.That(result.CrossRecipeDeduplicatedBytes, Is.EqualTo(result.UniqueLiteralBytes));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\new.bin"), Is.EqualTo(RandomBytes(200000)));
    }

    /// <summary>
    /// Preserve canonical UnityFS reconstruction without changing the pinned packing profile.
    /// </summary>
    [Test]
    public void CanonicalUnityFsRecipeReconstructsExactFrozenTarget()
    {
        byte[] data = RandomBytes(500000);
        using var input = new MemoryStream(SyntheticBundles.Bundle(false, ("data", data, false)));
        using var literals = new MemoryStream();
        using var canonical = new MemoryStream();
        BundleRecipe.Generate([], [], input, literals, canonical);
        var baseline = WriteContract(BASELINE, ("old.bin", data));
        var target = WriteContract(TARGET, ("native.bundle", canonical.ToArray()));

        var result = _pipeline.Publish(TARGET, target, Sources(baseline), ARTIFACT,
            new HashSet<string>(StringComparer.Ordinal) { "native.bundle" });
        Prepare(result, baseline);

        Assert.That(ReadManifest().Variants[0].Files[0].Recipe.PackingProfile,
            Is.EqualTo(BundleRecipe.CANONICAL_PACKING_PROFILE));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\native.bundle"), Is.EqualTo(canonical.ToArray()));
    }

    /// <summary>
    /// Reject canonicalization that would change an independently frozen target hash.
    /// </summary>
    [Test]
    public void NoncanonicalTargetCannotSilentlyChangeItsExpectedHash()
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("native.bundle", SyntheticBundles.Bundle(false,
            ("data", Enumerable.Repeat((byte)37, 200000).ToArray(), false))));

        Assert.Throws<InvalidDataException>(() => _pipeline.Publish(TARGET, target, Sources(baseline), ARTIFACT,
            new HashSet<string> { "native.bundle" }));
        Assert.That(_fileSystem.File.Exists(ARTIFACT + @"\release.json"), Is.False);
    }

    /// <summary>
    /// The shared tree contract rejects traversal, case aliases and file-directory collisions.
    /// </summary>
    /// <param name="second">The invalid competing target path.</param>
    [TestCase("../escape")]
    [TestCase("/absolute")]
    [TestCase("C:/absolute")]
    [TestCase("A.bin")]
    [TestCase("a.bin/child")]
    [TestCase("NUL")]
    public void UnsafeTargetPathsAreRejected(string second)
    {
        string hash = Convert.ToHexString(SHA256.HashData([]));
        Assert.Throws<InvalidDataException>(() => new StockBaselineManifest("target", "test",
            [new StockBaselineFile("a.bin", 0, hash), new StockBaselineFile(second, 0, hash)]));
    }

    /// <summary>
    /// Reject linked roots and overlapping stage destinations without altering input data.
    /// </summary>
    [Test]
    public void LinksAndOverlappingOutputRootsAreRejected()
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("new.bin", new byte[] { 2 }));
        string link = ROOT + @"\linked";
        _fileSystem.Directory.CreateSymbolicLink(link, TARGET);

        Assert.Throws<InvalidDataException>(() => _pipeline.Publish(link, target, Sources(baseline), ARTIFACT));
        Assert.Throws<InvalidDataException>(() => _pipeline.Publish(TARGET, target, Sources(baseline), TARGET + @"\out"));
        Assert.That(_fileSystem.File.ReadAllBytes(TARGET + @"\new.bin"), Is.EqualTo(new byte[] { 2 }));
    }

    /// <summary>
    /// Reject oversized, overlapping, missing, traversal and corrupted decoded chunk metadata.
    /// </summary>
    /// <param name="damage">The invalid manifest claim.</param>
    [TestCase("chunk-size")]
    [TestCase("overlap")]
    [TestCase("chunk-hash")]
    [TestCase("payload-index")]
    [TestCase("path")]
    [TestCase("recipe-version")]
    public void InvalidMetadataIsRejectedWithNoSurvivingStage(string damage)
    {
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));
        var target = WriteContract(TARGET, ("new.bin", RandomBytes(200000)));
        var result = Publish(target, baseline);
        var manifest = ReadManifest();
        switch (damage)
        {
            case "chunk-size": manifest.Chunks[0] = manifest.Chunks[0] with { Length = int.MaxValue }; break;
            case "overlap": manifest.Chunks[0] = manifest.Chunks[0] with { Offset = 1 }; break;
            case "chunk-hash": manifest.Chunks[0] = manifest.Chunks[0] with { Sha256 = new string('0', 64) }; break;
            case "payload-index": manifest.Variants[0].Files[0].PayloadChunks[0] = int.MaxValue; break;
            case "path": manifest.Variants[0].Files[0] = manifest.Variants[0].Files[0] with { Path = "../escape" }; break;
            case "recipe-version": manifest.Variants[0].Files[0].Recipe.Version = 999; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        _fileSystem.File.WriteAllBytes(ARTIFACT + @"\release.json", json);
        result = result with { ArtifactSha256 = Convert.ToHexString(SHA256.HashData(json)) };

        Assert.Throws<InvalidDataException>(() => Prepare(result, baseline));

        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);
    }

    /// <summary>
    /// Reject an oversized release marker before parsing it into metadata objects.
    /// </summary>
    [Test]
    public void OversizedManifestIsRejectedBeforeParsing()
    {
        _fileSystem.Directory.CreateDirectory(ARTIFACT);
        _fileSystem.File.WriteAllBytes(ARTIFACT + @"\release.json", new byte[ReleaseRecipePipeline.MAX_MANIFEST_BYTES + 1]);
        var baseline = WriteContract(BASELINE, ("old.bin", new byte[] { 1 }));

        Assert.Throws<InvalidDataException>(() => _pipeline.Prepare(ARTIFACT, new string('0', 64),
            new string('0', 64), BASELINE, baseline, STAGE));
        Assert.That(_fileSystem.Directory.Exists(STAGE), Is.False);
    }

    /// <summary>
    /// Reject malformed UnityFS allocation claims before the ordinary reader allocates arrays or caches.
    /// </summary>
    /// <param name="damage">The oversized metadata or storage block claim.</param>
    [TestCase("block-count")]
    [TestCase("storage-block")]
    [TestCase("metadata-bytes")]
    public void UnityFsPreflightRejectsUnboundedAllocationClaims(string damage)
    {
        byte[] bytes = SyntheticBundles.Bundle(false, ("data", RandomBytes(200000), false));
        using var input = new MemoryStream(bytes);
        var reader = new AssetsFileReader(input);
        var header = new AssetBundleHeader();
        header.Read(reader);
        int position = damage switch
        {
            "block-count" => checked((int)header.GetBundleInfoOffset() + 16),
            "storage-block" => checked((int)header.GetBundleInfoOffset() + 20),
            "metadata-bytes" => checked((int)input.Position - 12),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };
        uint value = damage == "storage-block" ? 1024 * 1024 + 1U : uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(position), value);
        input.Position = 17;
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<InvalidDataException>(() => BundleRecipe.ValidateReleaseInputBounds(input));

        Assert.That(input.Position, Is.EqualTo(17));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(1024 * 1024));
    }

    /// <summary>
    /// Publish from a single large raw block and reconstruct its exact bytes through the bounded reader.
    /// </summary>
    [Test]
    public void LargeRawUnityFsSourcePublishesAndReconstructsExactly()
    {
        byte[] data = RandomBytes(2 * 1024 * 1024 + 37);
        byte[] bytes = SingleRawBlock(data);
        using (var input = new MemoryStream(bytes))
        {
            BundleRecipe.ValidateReleaseInputBounds(input);
            using var bundle = UnityFsBundle.Open(input);
            var buffer = new byte[65536];
            long before = GC.GetAllocatedBytesForCurrentThread();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int count;
            while ((count = bundle.Data.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer, 0, count);
            Assert.That(hash.GetHashAndReset(), Is.EqualTo(SHA256.HashData(data)));
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(1024 * 1024));
        }
        var baseline = WriteContract(BASELINE, ("old.bundle", bytes));
        var target = WriteContract(TARGET, ("data.bin", data));
        var sources = new[] { new ReleaseRecipeSourceTree(BASELINE, baseline,
            [new ReleaseRecipeSource("old.bundle", BundleRecipeSourceKind.UnityFsData)]) };

        var result = _pipeline.Publish(TARGET, target, sources, ARTIFACT);
        Prepare(result, baseline);

        Assert.That(result.SourceReusedBytes, Is.EqualTo(data.Length));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\data.bin"), Is.EqualTo(data));
    }

    /// <summary>
    /// Reject inconsistent raw sizes, raw extents outside the file and oversized compressed blocks.
    /// </summary>
    /// <param name="damage">The invalid block claim.</param>
    [TestCase("raw-sizes")]
    [TestCase("raw-extent")]
    [TestCase("lz4-size")]
    [TestCase("lz4hc-size")]
    public void LargeBlocksRetainSizeAndPhysicalExtentGuards(string damage)
    {
        byte[] bytes = SingleRawBlock(RandomBytes(2 * 1024 * 1024 + 37));
        using var input = new MemoryStream(bytes);
        var header = new AssetBundleHeader();
        header.Read(new AssetsFileReader(input));
        int blockOffset = checked((int)header.GetBundleInfoOffset() + 20);
        if (damage == "raw-sizes")
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(blockOffset + 4), 1);
        else if (damage == "raw-extent")
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(blockOffset), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(blockOffset + 4), (uint)bytes.Length);
        }
        else
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(blockOffset + 8), damage == "lz4-size" ? (ushort)2 : (ushort)3);

        Assert.Throws<InvalidDataException>(() => BundleRecipe.ValidateReleaseInputBounds(input));
    }

    /// <summary>
    /// Native path validation cannot be replaced by the virtual filesystem test hook.
    /// </summary>
    [Test]
    public void NativeFilesystemCannotDisableStreamValidation()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseRecipePipeline(new Testably.Abstractions.RealFileSystem(), _ => { }));
    }

    /// <summary>
    /// Preserve complete literal-only releases and empty targets with no matching source descriptors.
    /// </summary>
    [Test]
    public void LiteralOnlyReleaseSupportsEmptyFilesAndNoSourceDescriptors()
    {
        var baseline = new StockBaselineManifest("empty-source", "Independent empty fixture", []);
        var target = WriteContract(TARGET, ("literal.bin", RandomBytes(100000)), ("zero", []));

        var result = _pipeline.Publish(TARGET, target,
            [new ReleaseRecipeSourceTree(BASELINE, baseline, [])], ARTIFACT);
        Prepare(result, baseline);

        Assert.That(result.SourceReusedBytes, Is.Zero);
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\literal.bin"), Is.EqualTo(RandomBytes(100000)));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\zero"), Is.Empty);
    }

    /// <summary>
    /// A canceled empty release cannot create output merely because it has no file loop.
    /// </summary>
    [Test]
    public void CanceledEmptyReleaseDoesNotCreateOutput()
    {
        var empty = new StockBaselineManifest("empty", "Independent empty fixture", []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => _pipeline.Publish(TARGET, empty,
            [new ReleaseRecipeSourceTree(BASELINE, empty, [])], ARTIFACT, ct: cancellation.Token));

        Assert.That(_fileSystem.Directory.Exists(ARTIFACT), Is.False);
    }

    /// <summary>
    /// Retain independently verified asset-aware range matching through release pack publication.
    /// </summary>
    /// <param name="valid">True for exact source bytes, false for a deliberately incorrect hint digest.</param>
    [TestCase(true)]
    [TestCase(false)]
    public void VerifiedRangeHintsRemainAvailableToThePublisher(bool valid)
    {
        byte[] data = RandomBytes(200000);
        byte[] changed = Enumerable.Repeat((byte)17, 68).Concat(data.Skip(12)).ToArray();
        var baseline = WriteContract(BASELINE, ("old.bin", data));
        var target = WriteContract(TARGET, ("changed.wem", changed));
        var hint = new BundleRecipeRangeHint
        {
            TargetEntryIndex = 0, TargetOffset = 68, SourceIndex = 0, SourceOffset = 12,
            Length = data.Length - 12,
            Sha256 = valid ? Convert.ToHexString(SHA256.HashData(data.AsSpan(12))) : new string('0', 64),
        };
        var source = Sources(baseline)[0] with
        {
            RangeHints = new Dictionary<string, IReadOnlyList<BundleRecipeRangeHint>>(StringComparer.Ordinal)
            {
                ["changed.wem"] = new[] { hint },
            },
        };
        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => _pipeline.Publish(TARGET, target, new[] { source }, ARTIFACT));
            Assert.That(_fileSystem.File.Exists(ARTIFACT + @"\release.json"), Is.False);
            return;
        }

        var result = _pipeline.Publish(TARGET, target, new[] { source }, ARTIFACT);
        Prepare(result, baseline);

        Assert.That(result.SourceReusedBytes, Is.EqualTo(data.Length - 12));
        Assert.That(result.LogicalLiteralBytes, Is.EqualTo(68));
        Assert.That(ReadManifest().Variants[0].Files[0].Recipe.MatchingProfile, Is.EqualTo(BundleRecipe.HINTED_MATCHING_PROFILE));
        Assert.That(_fileSystem.File.ReadAllBytes(STAGE + @"\changed.wem"), Is.EqualTo(changed));
    }

    private ReleaseRecipePublication Publish(StockBaselineManifest target, StockBaselineManifest baseline)
        => _pipeline.Publish(TARGET, target, Sources(baseline), ARTIFACT);

    private StockBaselineManifest Prepare(ReleaseRecipePublication result, StockBaselineManifest baseline)
        => _pipeline.Prepare(ARTIFACT, result.ArtifactSha256, result.TargetManifestSha256, BASELINE, baseline, STAGE);

    private static List<ReleaseRecipeSourceTree> Sources(StockBaselineManifest baseline) =>
        [new(BASELINE, baseline, [new ReleaseRecipeSource("old.bin", BundleRecipeSourceKind.RawFile)])];

    private ReleaseRecipeManifest ReadManifest() => JsonSerializer.Deserialize<ReleaseRecipeManifest>(
        _fileSystem.File.ReadAllBytes(ARTIFACT + @"\release.json"))!;

    private StockBaselineManifest WriteContract(string root, params (string Path, byte[] Bytes)[] entries)
    {
        var files = new List<StockBaselineFile>();
        foreach (var entry in entries)
        {
            string path = root + "\\" + entry.Path.Replace('/', '\\');
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
            _fileSystem.File.WriteAllBytes(path, entry.Bytes);
            files.Add(new StockBaselineFile(entry.Path, entry.Bytes.Length, Convert.ToHexString(SHA256.HashData(entry.Bytes))));
        }
        return new StockBaselineManifest(root, "Independent test fixture contract", files);
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(77231).NextBytes(bytes);
        return bytes;
    }

    private static byte[] SingleRawBlock(byte[] data)
    {
        var info = new AssetBundleBlockAndDirInfo
        {
            Hash = new Hash128(new byte[16]),
            BlockInfos = [new AssetBundleBlockInfo { DecompressedSize = (uint)data.Length, CompressedSize = (uint)data.Length }],
            DirectoryInfos = [new AssetBundleDirectoryInfo { Name = "data", DecompressedSize = data.Length }],
        };
        using var metadata = new MemoryStream();
        info.Write(new AssetsFileWriter(metadata) { BigEndian = true });
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS", Version = 8, GenerationVersion = "5.x.x", EngineVersion = SyntheticBundles.UNITY_VERSION,
            FileStreamHeader = new AssetBundleFSHeader
            {
                Flags = AssetBundleFSHeaderFlags.HasDirectoryInfo | AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart,
                CompressedSize = (uint)metadata.Length, DecompressedSize = (uint)metadata.Length,
            },
        };
        using var output = new MemoryStream();
        var writer = new AssetsFileWriter(output);
        header.Write(writer);
        writer.Align16();
        writer.Write(metadata.ToArray());
        writer.Align16();
        writer.Write(data);
        header.FileStreamHeader.TotalFileSize = output.Length;
        writer.Position = 0;
        header.Write(writer);
        return output.ToArray();
    }
}
