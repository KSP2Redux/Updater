using AssetsTools.NET;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using LZ4ps;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Verifies persisted range recipes, exact canonical output and streaming input contracts.
/// </summary>
public class BundleRecipeTest
{
    private const int BLOCK_SIZE = 128 * 1024;

    /// <summary>
    /// Reuses two bundles and native resource bytes in one target after deterministic JSON persistence.
    /// </summary>
    [Test]
    public void CrossBundleAndRawFileRangesRepartitionIntoOneExactTarget()
    {
        var fixture = CrossSourceFixture();
        byte[] json = JsonCodec.Write(fixture.Recipe);
        var restored = JsonCodec.Read<BundleRecipe>(json)!;
        Assert.That(JsonCodec.Write(restored), Is.EqualTo(json));
        Assert.That(JsonCodec.Write(fixture.Recipe), Is.EqualTo(json));
        using var first = new ProbeStream(fixture.Sources[0], maxReadResult: 7);
        using var second = new ProbeStream(fixture.Sources[1], maxReadResult: 11);
        using var raw = new ProbeStream(fixture.Sources[2], maxReadResult: 13);
        using var payload = new ProbeStream(fixture.Payload, maxReadResult: 17);
        using var output = new MemoryStream();
        first.Position = 19;
        second.Position = 23;
        raw.Position = 29;
        payload.Position = 31;

        restored.Reconstruct([first, second, raw], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(fixture.Expected));
        Assert.That(first.Position, Is.EqualTo(19));
        Assert.That(second.Position, Is.EqualTo(23));
        Assert.That(raw.Position, Is.EqualTo(29));
        Assert.That(payload.Position, Is.EqualTo(31));
        foreach (var input in new[] { first, second, raw, payload })
        {
            Assert.That(input.IsDisposed, Is.False);
            Assert.That(input.MaxReadRequest, Is.LessThanOrEqualTo(BLOCK_SIZE));
        }
    }

    /// <summary>
    /// Rejects tampering of each independently identified source before writing output.
    /// </summary>
    /// <param name="sourceIndex">The source file to tamper with.</param>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void TamperedIndividualSourceRestoresAllInputs(int sourceIndex)
    {
        var fixture = CrossSourceFixture();
        fixture.Sources[sourceIndex][0] ^= 0x40;
        using var first = new ProbeStream(fixture.Sources[0]);
        using var second = new ProbeStream(fixture.Sources[1]);
        using var raw = new ProbeStream(fixture.Sources[2]);
        using var payload = new ProbeStream(fixture.Payload);
        using var output = new MemoryStream();
        first.Position = 5;
        second.Position = 7;
        raw.Position = 11;
        payload.Position = 13;

        Assert.Throws<InvalidDataException>(() => fixture.Recipe.Reconstruct([first, second, raw], payload, output));

        Assert.That(output.Length, Is.Zero);
        Assert.That(first.Position, Is.EqualTo(5));
        Assert.That(second.Position, Is.EqualTo(7));
        Assert.That(raw.Position, Is.EqualTo(11));
        Assert.That(payload.Position, Is.EqualTo(13));
        foreach (var input in new[] { first, second, raw, payload })
        {
            Assert.That(input.IsDisposed, Is.False);
        }
    }

    /// <summary>
    /// Restores every input after a literal failure while multiple source bundles are open.
    /// </summary>
    /// <param name="truncatedPayload">True for early EOF, false for a changed literal byte.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void MultiSourceFailureAfterWritingRestoresInputPositions(bool truncatedPayload)
    {
        var fixture = CrossSourceFixture();
        byte[] literal = truncatedPayload ? fixture.Payload[..20] : fixture.Payload.ToArray();
        if (!truncatedPayload)
        {
            literal[19] ^= 0x40;
        }
        using var first = new ProbeStream(fixture.Sources[0]);
        using var second = new ProbeStream(fixture.Sources[1]);
        using var raw = new ProbeStream(fixture.Sources[2]);
        using var payload = new ProbeStream(literal, reportedLength: fixture.Payload.Length);
        using var output = new MemoryStream();
        first.Position = 5;
        second.Position = 7;
        raw.Position = 11;
        payload.Position = 13;

        if (truncatedPayload)
        {
            Assert.Throws<EndOfStreamException>(() => fixture.Recipe.Reconstruct([first, second, raw], payload, output));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => fixture.Recipe.Reconstruct([first, second, raw], payload, output));
        }

        Assert.That(output.Length, Is.GreaterThan(0));
        Assert.That(first.Position, Is.EqualTo(5));
        Assert.That(second.Position, Is.EqualTo(7));
        Assert.That(raw.Position, Is.EqualTo(11));
        Assert.That(payload.Position, Is.EqualTo(13));
        foreach (var input in new[] { first, second, raw, payload })
        {
            Assert.That(input.IsDisposed, Is.False);
        }
    }

    /// <summary>
    /// Accepts native storage blocks larger than the read-request cap without imposing a new memory budget.
    /// </summary>
    /// <param name="compressedSource">True to compress the single large source block.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void SourceStorageBlockSizeIsIndependentOfReadRequestLimit(bool compressedSource)
    {
        byte[] data = Pattern((4 * BLOCK_SIZE) + 31, 7);
        byte[] native = SingleStorageBlockBundle(data, compressedSource);
        byte[] expected = SyntheticBundles.Bundle(true, ("large.resS", data, false));
        var recipe = Recipe(native, [], expected, Entry("large.resS", 0, data.Length, Copy(0, data.Length)));
        using var source = new ProbeStream(native);
        using var payload = new MemoryStream();
        using var output = new MemoryStream();

        recipe.Reconstruct([source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(source.MaxReadRequest, Is.LessThanOrEqualTo(BLOCK_SIZE));
    }

    /// <summary>
    /// Ignores sparse unused source slots while validating their persisted descriptors.
    /// </summary>
    [Test]
    public void SparseUnusedSourcesAreNeitherOpenedNorHashed()
    {
        byte[] data = Pattern(99, 7);
        byte[] unusedBundle = TestGame.ContentBundle(true);
        byte[] expected = SyntheticBundles.Bundle(true, ("data.resS", data, false));
        var recipe = Recipe(null, [], expected, Entry("data.resS", 0, data.Length, Copy(0, data.Length, 2)));
        recipe.Sources = [RawSource([3]), BundleSource(unusedBundle), RawSource(data)];
        using var unused = new ProbeStream([255], canRead: false, canSeek: false);
        using var source = new ProbeStream(data);
        using var payload = new MemoryStream();
        using var output = new MemoryStream();
        unused.Position = 17;
        source.Position = 19;

        recipe.Reconstruct([null, unused, source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(unused.ReadCalls, Is.Zero);
        Assert.That(unused.Position, Is.EqualTo(17));
        Assert.That(unused.IsDisposed, Is.False);
        Assert.That(source.Position, Is.EqualTo(19));
        recipe.Sources[0].Kind = (BundleRecipeSourceKind)99;
        using var rejectedOutput = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => recipe.Reconstruct([null, unused, source], payload, rejectedOutput));
        Assert.That(rejectedOutput.Length, Is.Zero);
    }

    /// <summary>
    /// Rejects missing referenced source streams and inconsistent stream slot counts.
    /// </summary>
    /// <param name="missingSlot">True for a null referenced slot, false for a missing list slot.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void ReferencedSourcesCannotBeOmitted(bool missingSlot)
    {
        byte[] expected = SyntheticBundles.Bundle(true, ("data.resS", new byte[] { 7 }, false));
        var recipe = Recipe(null, [], expected, Entry("data.resS", 0, 1, Copy(0, 1)));
        recipe.Sources.Add(RawSource([7]));
        Stream?[] inputs = missingSlot ? [null] : [];
        using var payload = new MemoryStream();
        using var output = new MemoryStream();

        if (missingSlot)
        {
            Assert.Throws<ArgumentNullException>(() => recipe.Reconstruct(inputs, payload, output));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => recipe.Reconstruct(inputs, payload, output));
        }

        Assert.That(output.Length, Is.Zero);
    }

    /// <summary>
    /// Constructs literal-only and empty bundles without any source file descriptor or stream.
    /// </summary>
    /// <param name="emptyTarget">True to reconstruct an empty directory.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void TargetsWithoutCopyRangesNeedNoDummySource(bool emptyTarget)
    {
        byte[] literal = Pattern(BLOCK_SIZE + 13, 17);
        byte[] expected = emptyTarget ? SyntheticBundles.Bundle(true) : SyntheticBundles.Bundle(true, ("new.resS", literal, false));
        BundleRecipeEntry[] entries = emptyTarget ? [] : [Entry("new.resS", 0, literal.Length, Literal(0, literal.Length))];
        var recipe = Recipe(null, literal, expected, entries);
        var restored = JsonCodec.Read<BundleRecipe>(JsonCodec.Write(recipe))!;
        using var payload = new ProbeStream(literal, maxReadResult: 7);
        using var output = new MemoryStream();
        payload.Position = 3;

        restored.Reconstruct([], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(payload.Position, Is.EqualTo(3));
        Assert.That(payload.IsDisposed, Is.False);
    }

    /// <summary>
    /// Copies native bundle file bytes when the descriptor explicitly selects raw coordinates.
    /// </summary>
    [Test]
    public void RawCoordinatesDoNotInterpretUnityFsHeaders()
    {
        byte[] native = TestGame.ContentBundle(true);
        byte[] content = native.Skip(5).Take(native.Length - 10).ToArray();
        byte[] expected = SyntheticBundles.Bundle(true, ("native.resS", content, false));
        var recipe = Recipe(null, [], expected, Entry("native.resS", 0, content.Length, Copy(5, content.Length)));
        recipe.Sources.Add(RawSource(native));
        using var source = new ProbeStream(native, maxReadResult: 7);
        using var payload = new MemoryStream();
        using var output = new MemoryStream();

        recipe.Reconstruct([source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
    }

    /// <summary>
    /// Pins independent canonical codec cases so writer or dependency changes require deliberate handling.
    /// </summary>
    /// <param name="profileCase">The representative content layout.</param>
    /// <param name="expectedHash">The SHA-256 recorded for the canonical profile.</param>
    [TestCase("raw-fallback", "c6235827fbfa9c4e732c606bf7adf0786c2423d92f5fb944bf561c2121e51c70")]
    [TestCase("entry-spanning", "42dc9d994ce161cb4f8740f7201d01ef33f6940f3fd6a32e22ec0c217f7d9bd1")]
    [TestCase("long-match", "d5ab7e5fd087f03a41a4c800a97da8f5fda578b2c59d744aa8b5cb3ee7f81c8a")]
    public void CanonicalPackingCasesHavePinnedExactHashes(string profileCase, string expectedHash)
    {
        (string Name, byte[] Data, bool Serialized)[] contents = profileCase switch
        {
            "raw-fallback" => [("noise.resS", Noise(BLOCK_SIZE + 19), false)],
            "entry-spanning" => [("CAB-main", Pattern(BLOCK_SIZE - 23, 37), true), ("main.resS", Pattern(BLOCK_SIZE + 78, 37), false)],
            "long-match" => [("long.resS", Enumerable.Range(0, (3 * BLOCK_SIZE) + 77).Select(index => (byte)((index % 8191) * 19)).ToArray(), false)],
            _ => throw new ArgumentException("Unknown canonical profile case.", nameof(profileCase)),
        };
        byte[] expected = SyntheticBundles.Bundle(true, contents);
        Assert.That(Hex.Sha256(expected), Is.EqualTo(expectedHash));
        byte[] literals = contents.SelectMany(entry => entry.Data).ToArray();
        var entries = new List<BundleRecipeEntry>();
        long offset = 0;
        foreach (var entry in contents)
        {
            entries.Add(Entry(entry.Name, entry.Serialized ? 4u : 0u, entry.Data.Length, Literal(offset, entry.Data.Length)));
            offset += entry.Data.Length;
        }
        var recipe = Recipe(null, literals, expected, entries.ToArray());
        // The persisted identity is the golden, rather than an expectation derived from this writer.
        recipe.TargetSha256 = expectedHash;
        using var payload = new ProbeStream(literals);
        using var output = new MemoryStream();

        recipe.Reconstruct([], payload, output);

        Assert.That(Hex.Sha256(output.ToArray()), Is.EqualTo(expectedHash));
        using var bundle = UnityFsBundle.Open(new MemoryStream(output.ToArray()));
        Assert.That(bundle.Blocks[0].Flags & 0x3F, Is.EqualTo(profileCase == "raw-fallback" ? 0 : 3));
    }

    /// <summary>
    /// Reconstructs reordered source and literal ranges across entry and storage block boundaries.
    /// </summary>
    /// <param name="compressedSource">True to compress the source storage blocks.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void MixedRangesSurviveJsonRoundTripAndPreserveResourceEntries(bool compressedSource)
    {
        byte[] first = Pattern(160_000, 7);
        byte[] second = Pattern(310_000, 13);
        byte[] sourceBytes = SyntheticBundles.Bundle(compressedSource, ("CAB-source", first, true), ("source.resS", second, false));
        byte[] sourceData = first.Concat(second).ToArray();
        byte[] payloadBytes = Pattern(180_000, 31);
        byte[] changed = sourceData.Skip(130_999).Take(140_003)
            .Concat(payloadBytes.Skip(5).Take(160_013)).Concat(sourceData.Skip(2).Take(101)).ToArray();
        byte[] resource = payloadBytes.Skip(179_990).Take(10)
            .Concat(sourceData.Skip(1).Take(140_000)).Concat(payloadBytes.Take(33)).ToArray();
        byte[] expected = SyntheticBundles.Bundle(true, ("CAB-target", changed, true), ("target.resS", resource, false), ("empty.resS", [], false));
        var recipe = Recipe(sourceBytes, payloadBytes, expected,
            Entry("CAB-target", 4, changed.Length,
                Copy(130_999, 140_003), Literal(5, 160_013), Copy(2, 101)),
            Entry("target.resS", 0, resource.Length, Literal(179_990, 10), Copy(1, 140_000), Literal(0, 33)),
            Entry("empty.resS", 0, 0, Copy(sourceData.Length, 0), Literal(payloadBytes.Length, 0)));
        var restored = JsonCodec.Read<BundleRecipe>(JsonCodec.Write(recipe))!;
        using var source = new ProbeStream(sourceBytes, maxReadResult: 257);
        using var payload = new ProbeStream(payloadBytes, maxReadResult: 11);
        using var output = new MemoryStream();
        source.Position = 19;
        payload.Position = 23;

        restored.Reconstruct([source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(source.Position, Is.EqualTo(19));
        Assert.That(payload.Position, Is.EqualTo(23));
        Assert.That(output.Position, Is.EqualTo(expected.Length));
        Assert.That(source.IsDisposed || payload.IsDisposed, Is.False);
        Assert.That(source.MaxReadRequest, Is.LessThanOrEqualTo(BLOCK_SIZE));
        Assert.That(payload.MaxReadRequest, Is.LessThanOrEqualTo(BLOCK_SIZE));
        using var rebuilt = UnityFsBundle.Open(new MemoryStream(output.ToArray()));
        Assert.That(rebuilt.Directory.Select(entry => entry.Name), Is.EqualTo(new[] { "CAB-target", "target.resS", "empty.resS" }));
        Assert.That(rebuilt.Directory.Select(entry => entry.Flags), Is.EqualTo(new uint[] { 4, 0, 0 }));
        Assert.That(rebuilt.Directory.Select(entry => entry.Offset), Is.EqualTo(new long[] { 0, changed.Length, changed.Length + resource.Length }));
        Assert.That(UnityFsBundle.ReadExactly(rebuilt.Data, changed.Length + resource.Length), Is.EqualTo(changed.Concat(resource).ToArray()));
    }

    /// <summary>
    /// Reconstructs an unchanged canonical bundle with serialized content and resource blocks byte for byte.
    /// </summary>
    [Test]
    public void UnchangedCanonicalSourceIsByteIdenticalWithBoundedReads()
    {
        byte[] sourceBytes = TestGame.ContentBundle(compress: true);
        using var bundle = UnityFsBundle.Open(new MemoryStream(sourceBytes));
        var entries = bundle.Directory.Select(entry => Entry(entry.Name, entry.Flags, entry.DecompressedSize,
            Copy(entry.Offset, 0), Copy(entry.Offset, entry.DecompressedSize), Copy(entry.Offset + entry.DecompressedSize, 0))).ToArray();
        var recipe = Recipe(sourceBytes, [], sourceBytes, entries);
        recipe.Sources[0].Sha256 = recipe.Sources[0].Sha256.ToUpperInvariant();
        recipe.TargetSha256 = recipe.TargetSha256.ToUpperInvariant();
        using var source = new ProbeStream(sourceBytes, maxReadResult: 7);
        using var payload = new ProbeStream([]);
        using var output = new MemoryStream();

        recipe.Reconstruct([source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(sourceBytes));
        Assert.That(source.MaxReadRequest, Is.LessThanOrEqualTo(BLOCK_SIZE));
        Assert.That(source.ReadCalls, Is.GreaterThan(100));
        // Pin canonical bytes independently of the writer used to construct other expectations.
        Assert.That(Hex.Sha256(output.ToArray()), Is.EqualTo("5759c882216881b2f776323721f48730b88445a08a166c247afab2fb952cc1ae"));
    }

    /// <summary>
    /// Reconstructs header format variants and arbitrary directory flags without interpreting entry contents.
    /// </summary>
    /// <param name="formatVersion">The target UnityFS format version.</param>
    [TestCase(6u)]
    [TestCase(7u)]
    [TestCase(8u)]
    public void TargetHeaderAndDirectoryFlagsAreWrittenExactly(uint formatVersion)
    {
        byte[] data = Pattern(BLOCK_SIZE + 1, 19);
        byte[] sourceBytes = SyntheticBundles.Bundle(false, ("source.resS", data, false));
        var targetHeader = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = formatVersion,
            GenerationVersion = "recipe-generation",
            EngineVersion = "6000.6.0f1",
        };
        using var expectedStream = new MemoryStream();
        using var content = new MemoryStream(data);
        BundleRewriter.WriteBundle(expectedStream, targetHeader,
            new[] { new KeyValuePair<AssetBundleDirectoryInfo, Stream>(new AssetBundleDirectoryInfo { Name = "../metadata-only.resS", Flags = 0x100 }, content) }, true);
        byte[] expected = expectedStream.ToArray();
        var recipe = Recipe(sourceBytes, [], expected, Entry("../metadata-only.resS", 0x100, data.Length, Copy(0, data.Length)));
        using var source = new MemoryStream(sourceBytes, writable: false);
        using var payload = new MemoryStream();
        using var output = new MemoryStream();

        recipe.Reconstruct([source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
    }

    /// <summary>
    /// Verifies bounded literal reads and the canonical raw-block fallback for incompressible content.
    /// </summary>
    [Test]
    public void LargeLiteralPayloadStreamsIntoRawAndCompressedBlocks()
    {
        byte[] payloadBytes = new byte[(4 * BLOCK_SIZE) + 17];
        new Random(1741).NextBytes(payloadBytes.AsSpan(0, 2 * BLOCK_SIZE));
        byte[] sourceBytes = SyntheticBundles.Bundle(false, ("unused", new byte[] { 1 }, false));
        byte[] expected = SyntheticBundles.Bundle(true, ("literal.resS", payloadBytes, false));
        var recipe = Recipe(sourceBytes, payloadBytes, expected, Entry("literal.resS", 0, payloadBytes.Length, Literal(0, payloadBytes.Length)));
        using var source = new ProbeStream(sourceBytes);
        using var payload = new ProbeStream(payloadBytes);
        using var output = new MemoryStream();

        recipe.Reconstruct([source], payload, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(payload.MaxReadRequest, Is.EqualTo(BLOCK_SIZE));
        using var bundle = UnityFsBundle.Open(new MemoryStream(output.ToArray()));
        Assert.That(bundle.Blocks.Any(block => (block.Flags & 0x3F) == 0), Is.True);
        Assert.That(bundle.Blocks.Any(block => (block.Flags & 0x3F) == 3), Is.True);
    }

    /// <summary>
    /// Caps bundle reader requests even when the source directory metadata exceeds one data block.
    /// </summary>
    [Test]
    public void LargeSourceDirectoryDoesNotForceUnboundedReadRequests()
    {
        var sourceEntries = Enumerable.Range(0, 1500)
            .Select(index => ($"{index}-{new string('a', 128)}", Array.Empty<byte>(), false))
            .Append(("data.resS", new byte[] { 5 }, false)).ToArray();
        byte[] sourceBytes = SyntheticBundles.Bundle(true, sourceEntries);
        byte[] expected = SyntheticBundles.Bundle(true, ("target.resS", new byte[] { 5 }, false));
        var recipe = Recipe(sourceBytes, [], expected, Entry("target.resS", 0, 1, Copy(0, 1)));
        using var source = new ProbeStream(sourceBytes);
        using var payload = new ProbeStream([]);
        using var output = new MemoryStream();

        recipe.Reconstruct([source], payload, output);

        Assert.That(sourceBytes.Length, Is.GreaterThan(BLOCK_SIZE));
        Assert.That(source.MaxReadRequest, Is.EqualTo(BLOCK_SIZE));
        Assert.That(output.ToArray(), Is.EqualTo(expected));
    }

    /// <summary>
    /// Rejects malformed source container metadata even when the file hash matches the recipe.
    /// </summary>
    /// <param name="fault">The malformed source metadata.</param>
    [TestCase("signature")]
    [TestCase("file-length")]
    [TestCase("negative-directory-offset")]
    [TestCase("directory-past-end")]
    [TestCase("empty-block")]
    [TestCase("oversized-block")]
    [TestCase("raw-block-size")]
    [TestCase("unsupported-compression")]
    [TestCase("truncated-block")]
    public void MalformedSourceContainersLeaveInputsOpen(string fault)
    {
        byte[] sourceBytes = SyntheticBundles.Bundle(false, ("data.resS", Pattern(200, 7), false));
        byte[] original = sourceBytes.ToArray();
        using (var editable = new MemoryStream(sourceBytes, writable: true))
        using (var bundle = UnityFsBundle.Open(editable))
        {
            switch (fault)
            {
                case "signature": break;
                case "file-length": bundle.Header.FileStreamHeader.TotalFileSize++; break;
                case "negative-directory-offset": bundle.Directory[0].Offset = -1; break;
                case "directory-past-end": bundle.Directory[0].DecompressedSize++; break;
                case "empty-block": bundle.Blocks[0].DecompressedSize = 0; break;
                case "oversized-block": bundle.Blocks[0].DecompressedSize = uint.MaxValue; break;
                case "raw-block-size": bundle.Blocks[0].CompressedSize--; break;
                case "unsupported-compression": bundle.Blocks[0].Flags = 1; break;
                case "truncated-block":
                    bundle.Header.FileStreamHeader.TotalFileSize--;
                    break;
                default: throw new ArgumentException("Unknown test fault.", nameof(fault));
            }
            editable.Position = 0;
            bundle.Header.Write(new AssetsFileWriter(editable));
            editable.Position = bundle.Header.GetBundleInfoOffset();
            var info = new AssetBundleBlockAndDirInfo
            {
                Hash = new Hash128(new byte[16]),
                BlockInfos = bundle.Blocks,
                DirectoryInfos = bundle.Directory,
            };
            info.Write(new AssetsFileWriter(editable) { BigEndian = true });
        }
        if (fault == "signature")
        {
            sourceBytes[0] = (byte)'X';
        }
        if (fault == "truncated-block")
        {
            sourceBytes = sourceBytes[..^1];
        }
        var recipe = Recipe(original, [], original, Entry("probe", 0, 0, Copy(0, 0)));
        recipe.Sources[0].Sha256 = Hex.Sha256(sourceBytes);
        recipe.Sources[0].Length = sourceBytes.Length;
        if (fault == "empty-block")
        {
            recipe.Sources[0].DataLength = 0;
        }
        if (fault == "oversized-block")
        {
            recipe.Sources[0].DataLength = uint.MaxValue;
        }
        using var source = new ProbeStream(sourceBytes);
        using var payload = new ProbeStream([]);
        using var output = new MemoryStream();
        source.Position = 5;

        Assert.Throws<InvalidDataException>(() => recipe.Reconstruct([source], payload, output));

        Assert.That(source.IsDisposed || payload.IsDisposed, Is.False);
        Assert.That(source.Position, Is.EqualTo(5));
        Assert.That(output.Length, Is.Zero);
    }

    /// <summary>
    /// Rejects invalid persisted metadata or segment ranges before any target bytes are written.
    /// </summary>
    /// <param name="fault">The invalid recipe field or range.</param>
    [TestCase("version")]
    [TestCase("future-version")]
    [TestCase("profile")]
    [TestCase("source-hash-format")]
    [TestCase("target-hash-format")]
    [TestCase("source-length")]
    [TestCase("payload-length")]
    [TestCase("negative-source-length")]
    [TestCase("negative-data-length")]
    [TestCase("negative-payload-length")]
    [TestCase("negative-target-length")]
    [TestCase("source-header")]
    [TestCase("sources-null")]
    [TestCase("source-null")]
    [TestCase("source-kind")]
    [TestCase("source-index-negative")]
    [TestCase("source-index-past-end")]
    [TestCase("literal-source-index")]
    [TestCase("raw-source-length")]
    [TestCase("raw-source-header")]
    [TestCase("target-header")]
    [TestCase("header-version")]
    [TestCase("header-string")]
    [TestCase("target-flags")]
    [TestCase("directory-null")]
    [TestCase("entry-null")]
    [TestCase("name-null")]
    [TestCase("name-terminator")]
    [TestCase("duplicate-name")]
    [TestCase("negative-entry-length")]
    [TestCase("overflow-entry-length")]
    [TestCase("segments-null")]
    [TestCase("segment-null")]
    [TestCase("segment-kind")]
    [TestCase("copy-negative-offset")]
    [TestCase("copy-negative-length")]
    [TestCase("copy-past-end")]
    [TestCase("copy-overflow-offset")]
    [TestCase("copy-overflow-length")]
    [TestCase("literal-negative-offset")]
    [TestCase("literal-negative-length")]
    [TestCase("literal-past-end")]
    [TestCase("literal-overflow-offset")]
    [TestCase("literal-overflow-length")]
    [TestCase("entry-underfilled")]
    [TestCase("entry-overfilled")]
    public void MalformedRecipesDoNotWriteOutput(string fault)
    {
        byte[] sourceBytes = SyntheticBundles.Bundle(true, ("source", new byte[] { 1, 2, 3 }, false));
        byte[] payloadBytes = new byte[] { 4, 5, 6 };
        byte[] expected = SyntheticBundles.Bundle(true, ("target", new byte[] { 1, 4 }, false));
        var recipe = Recipe(sourceBytes, payloadBytes, expected, Entry("target", 0, 2, Copy(0, 1), Literal(0, 1)));
        var entry = recipe.Entries[0];
        var copy = entry.Segments[0];
        var literal = entry.Segments[1];
        switch (fault)
        {
            case "version": recipe.Version = 0; break;
            case "future-version": recipe.Version = int.MaxValue; break;
            case "profile": recipe.PackingProfile = "unknown"; break;
            case "source-hash-format": recipe.Sources[0].Sha256 = "short"; break;
            case "target-hash-format": recipe.TargetSha256 = new string('x', 64); break;
            case "source-length": recipe.Sources[0].Length++; break;
            case "payload-length": recipe.PayloadLength++; break;
            case "negative-source-length": recipe.Sources[0].Length = -1; break;
            case "negative-data-length": recipe.Sources[0].DataLength = -1; break;
            case "negative-payload-length": recipe.PayloadLength = -1; break;
            case "negative-target-length": recipe.TargetLength = -1; break;
            case "source-header": recipe.Sources[0].Header = null; break;
            case "sources-null": recipe.Sources = null!; break;
            case "source-null": recipe.Sources[0] = null!; break;
            case "source-kind": recipe.Sources[0].Kind = (BundleRecipeSourceKind)0; break;
            case "source-index-negative": copy.SourceIndex = -1; break;
            case "source-index-past-end": copy.SourceIndex = recipe.Sources.Count; break;
            case "literal-source-index": literal.SourceIndex = 0; break;
            case "raw-source-length": recipe.Sources[0].Kind = BundleRecipeSourceKind.RawFile; recipe.Sources[0].Header = null; break;
            case "raw-source-header": recipe.Sources[0].Kind = BundleRecipeSourceKind.RawFile; recipe.Sources[0].DataLength = recipe.Sources[0].Length; break;
            case "target-header": recipe.TargetHeader = null; break;
            case "header-version": recipe.TargetHeader!.FormatVersion = uint.MaxValue; break;
            case "header-string": recipe.TargetHeader!.EngineVersion = "bad\0version"; break;
            case "target-flags": recipe.TargetHeader!.Flags = 0; break;
            case "directory-null": recipe.Entries = null!; break;
            case "entry-null": recipe.Entries.Add(null!); break;
            case "name-null": entry.Name = null!; break;
            case "name-terminator": entry.Name = "bad\0name"; break;
            case "duplicate-name": recipe.Entries.Add(Entry(entry.Name, 0, 0)); break;
            case "negative-entry-length": entry.Length = -1; break;
            case "overflow-entry-length": entry.Length = long.MaxValue; break;
            case "segments-null": entry.Segments = null!; break;
            case "segment-null": entry.Segments.Add(null!); break;
            case "segment-kind": copy.Kind = (BundleRecipeSegmentKind)0; break;
            case "copy-negative-offset": copy.Offset = -1; break;
            case "copy-negative-length": copy.Length = -1; break;
            case "copy-past-end": copy.Offset = recipe.Sources[0].DataLength; break;
            case "copy-overflow-offset": copy.Offset = long.MaxValue; break;
            case "copy-overflow-length": copy.Offset = 1; copy.Length = long.MaxValue; break;
            case "literal-negative-offset": literal.Offset = -1; break;
            case "literal-negative-length": literal.Length = -1; break;
            case "literal-past-end": literal.Offset = payloadBytes.Length; break;
            case "literal-overflow-offset": literal.Offset = long.MaxValue; break;
            case "literal-overflow-length": literal.Offset = 1; literal.Length = long.MaxValue; break;
            case "entry-underfilled": entry.Length++; break;
            case "entry-overfilled": entry.Length--; break;
            default: throw new ArgumentException("Unknown test fault.", nameof(fault));
        }
        using var source = new MemoryStream(sourceBytes);
        using var payload = new MemoryStream(payloadBytes);
        using var output = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => recipe.Reconstruct([source], payload, output));

        Assert.That(output.Length, Is.Zero);
        Assert.That(source.CanRead && payload.CanRead && output.CanWrite, Is.True);
    }

    /// <summary>
    /// Rejects source identity and decoded metadata mismatches before writing target data.
    /// </summary>
    /// <param name="fault">The expected source value to change.</param>
    [TestCase("hash")]
    [TestCase("data-length")]
    [TestCase("flags")]
    [TestCase("version")]
    [TestCase("generation")]
    [TestCase("engine")]
    public void SourceMismatchRestoresInputsAndLeavesOutputEmpty(string fault)
    {
        byte[] sourceBytes = TestGame.ContentBundle(true);
        var recipe = Recipe(sourceBytes, [], sourceBytes, Entry("probe", 0, 0, Copy(0, 0)));
        switch (fault)
        {
            case "hash": recipe.Sources[0].Sha256 = new string('0', 64); break;
            case "data-length": recipe.Sources[0].DataLength++; break;
            case "flags": recipe.Sources[0].Header!.Flags ^= 1; break;
            case "version": recipe.Sources[0].Header!.FormatVersion = 7; break;
            case "generation": recipe.Sources[0].Header!.GenerationVersion = "different"; break;
            case "engine": recipe.Sources[0].Header!.EngineVersion = "different"; break;
            default: throw new ArgumentException("Unknown test fault.", nameof(fault));
        }
        using var source = new ProbeStream(sourceBytes);
        using var payload = new ProbeStream([]);
        using var output = new MemoryStream();
        source.Position = 5;

        Assert.Throws<InvalidDataException>(() => recipe.Reconstruct([source], payload, output));

        Assert.That(output.Length, Is.Zero);
        Assert.That(source.Position, Is.EqualTo(5));
        Assert.That(source.IsDisposed || payload.IsDisposed, Is.False);
    }

    /// <summary>
    /// Rejects reconstructed length or SHA-256 mismatches while preserving caller ownership.
    /// </summary>
    /// <param name="fault">The target identity or payload fault.</param>
    [TestCase("hash")]
    [TestCase("length")]
    [TestCase("payload")]
    public void TargetMismatchNeverReportsSuccessfulReconstruction(string fault)
    {
        byte[] sourceBytes = SyntheticBundles.Bundle(true, ("source", new byte[] { 1 }, false));
        byte[] payloadBytes = new byte[] { 2, 3, 4 };
        byte[] expected = SyntheticBundles.Bundle(true, ("target", payloadBytes, false));
        var recipe = Recipe(sourceBytes, payloadBytes, expected, Entry("target", 0, payloadBytes.Length, Literal(0, payloadBytes.Length)));
        switch (fault)
        {
            case "hash": recipe.TargetSha256 = new string('0', 64); break;
            case "length": recipe.TargetLength++; break;
            case "payload": payloadBytes[1] ^= 0x40; break;
            default: throw new ArgumentException("Unknown test fault.", nameof(fault));
        }
        using var source = new ProbeStream(sourceBytes);
        using var payload = new ProbeStream(payloadBytes);
        using var output = new MemoryStream();
        source.Position = 3;
        payload.Position = 2;

        Assert.Throws<InvalidDataException>(() => recipe.Reconstruct([source], payload, output));

        Assert.That(output.Length, Is.GreaterThan(0));
        Assert.That(source.Position, Is.EqualTo(3));
        Assert.That(payload.Position, Is.EqualTo(2));
        Assert.That(source.IsDisposed || payload.IsDisposed, Is.False);
        Assert.That(output.CanWrite, Is.True);
    }

    /// <summary>
    /// Rejects source and payload streams that end before their advertised length.
    /// </summary>
    /// <param name="truncateSource">True to truncate the source, false to truncate literal payload.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void TruncatedStreamsCannotSilentlyShortenEntries(bool truncateSource)
    {
        byte[] sourceBytes = SyntheticBundles.Bundle(true, ("source", new byte[] { 1 }, false));
        byte[] payloadBytes = new byte[] { 2, 3, 4 };
        byte[] expected = SyntheticBundles.Bundle(true, ("target", payloadBytes, false));
        var recipe = Recipe(sourceBytes, payloadBytes, expected, Entry("target", 0, payloadBytes.Length, Literal(0, payloadBytes.Length)));
        byte[] actualSource = truncateSource ? sourceBytes[..^1] : sourceBytes;
        byte[] actualPayload = truncateSource ? payloadBytes : payloadBytes[..^1];
        if (truncateSource)
        {
            // Even an expected hash of the readable prefix must not hide a truncated file.
            recipe.Sources[0].Sha256 = Hex.Sha256(actualSource);
            recipe.Entries[0].Segments.Insert(0, Copy(0, 0));
        }
        using var source = new ProbeStream(actualSource, reportedLength: sourceBytes.Length);
        using var payload = new ProbeStream(actualPayload, reportedLength: payloadBytes.Length);
        using var output = new MemoryStream();

        Assert.Throws<EndOfStreamException>(() => recipe.Reconstruct([source], payload, output));

        Assert.That(source.Position, Is.Zero);
        Assert.That(payload.Position, Is.Zero);
        Assert.That(source.IsDisposed || payload.IsDisposed, Is.False);
    }

    /// <summary>
    /// Rejects unsuitable or aliased stream arguments without disposing caller resources.
    /// </summary>
    /// <param name="fault">The unsuitable stream condition.</param>
    [TestCase("source-unreadable")]
    [TestCase("source-unseekable")]
    [TestCase("payload-unreadable")]
    [TestCase("payload-unseekable")]
    [TestCase("output-unreadable")]
    [TestCase("output-unwritable")]
    [TestCase("output-unseekable")]
    [TestCase("output-nonempty")]
    [TestCase("output-position")]
    [TestCase("source-payload-alias")]
    [TestCase("source-output-alias")]
    [TestCase("payload-output-alias")]
    public void StreamPreconditionsLeaveCallerResourcesOpen(string fault)
    {
        byte[] sourceBytes = SyntheticBundles.Bundle(true, ("source", new byte[] { 1 }, false));
        var recipe = Recipe(sourceBytes, [], sourceBytes, Entry("probe", 0, 0, Copy(0, 0)));
        using var source = new ProbeStream(sourceBytes, canRead: fault != "source-unreadable", canSeek: fault != "source-unseekable");
        using var payload = new ProbeStream([], canRead: fault != "payload-unreadable", canSeek: fault != "payload-unseekable");
        using var output = new ProbeStream([], canRead: fault != "output-unreadable", canSeek: fault != "output-unseekable", canWrite: fault != "output-unwritable");
        if (fault == "output-nonempty")
        {
            output.SetLength(1);
        }
        if (fault == "output-position")
        {
            output.Position = 1;
        }
        Stream payloadArgument = fault == "source-payload-alias" ? source : payload;
        if (fault == "source-payload-alias")
        {
            recipe.PayloadLength = source.Length;
        }
        Stream outputArgument = fault switch
        {
            "source-output-alias" => source,
            "payload-output-alias" => payload,
            _ => output,
        };

        Assert.Throws<ArgumentException>(() => recipe.Reconstruct([source], payloadArgument, outputArgument));

        Assert.That(source.IsDisposed || payload.IsDisposed || output.IsDisposed, Is.False);
    }

    private static BundleRecipe Recipe(byte[]? source, byte[] payload, byte[] target, params BundleRecipeEntry[] entries)
    {
        using var targetBundle = UnityFsBundle.Open(new MemoryStream(target));
        return new BundleRecipe
        {
            Version = BundleRecipe.CURRENT_VERSION,
            PackingProfile = BundleRecipe.CANONICAL_PACKING_PROFILE,
            Sources = source is null ? [] : [BundleSource(source)],
            PayloadLength = payload.Length,
            TargetLength = target.Length,
            TargetSha256 = Hex.Sha256(target),
            TargetHeader = Header(targetBundle.Header),
            Entries = entries.ToList(),
        };
    }

    private static BundleRecipeSource BundleSource(byte[] bytes)
    {
        using var bundle = UnityFsBundle.Open(new MemoryStream(bytes));
        return new BundleRecipeSource
        {
            Kind = BundleRecipeSourceKind.UnityFsData,
            Length = bytes.Length,
            DataLength = bundle.Data.Length,
            Sha256 = Hex.Sha256(bytes),
            Header = Header(bundle.Header),
        };
    }

    private static BundleRecipeSource RawSource(byte[] bytes) => new()
    {
        Kind = BundleRecipeSourceKind.RawFile,
        Length = bytes.Length,
        DataLength = bytes.Length,
        Sha256 = Hex.Sha256(bytes),
    };

    private static (BundleRecipe Recipe, byte[][] Sources, byte[] Payload, byte[] Expected) CrossSourceFixture()
    {
        byte[] first = Pattern(260_000, 7);
        byte[] second = Pattern(180_000, 13);
        byte[] raw = Pattern(90_000, 17);
        byte[] payload = Pattern(30_000, 31);
        byte[] firstBundle = SyntheticBundles.Bundle(true, ("CAB-one", first, true));
        byte[] secondBundle = SyntheticBundles.Bundle(false, ("two.resS", second, false));
        byte[] content = first.Skip(130_001).Take(129_999).Concat(second.Skip(20).Take(140_000))
            .Concat(raw.Skip(101).Take(80_000)).Concat(payload.Skip(19).Take(211)).ToArray();
        byte[] resource = raw.Skip(70_000).Take(19_999).Concat(first.Skip(3).Take(131_077)).Concat(second.Take(100)).ToArray();
        byte[] expected = SyntheticBundles.Bundle(true, ("CAB-combined", content, true), ("combined.resS", resource, false));
        var recipe = Recipe(firstBundle, payload, expected,
            Entry("CAB-combined", 4, content.Length, Copy(130_001, 129_999, 0), Copy(20, 140_000, 1), Copy(101, 80_000, 2), Literal(19, 211)),
            Entry("combined.resS", 0, resource.Length, Copy(70_000, 19_999, 2), Copy(3, 131_077, 0), Copy(0, 100, 1)));
        recipe.Sources.Add(BundleSource(secondBundle));
        recipe.Sources.Add(RawSource(raw));
        return (recipe, [firstBundle, secondBundle, raw], payload, expected);
    }

    private static byte[] Noise(int length)
    {
        var bytes = new byte[length];
        uint state = 0x9E3779B9;
        for (int index = 0; index < bytes.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[index] = (byte)state;
        }
        return bytes;
    }

    private static byte[] SingleStorageBlockBundle(byte[] data, bool compress)
    {
        byte[] storage = compress ? LZ4Codec.Encode32HC(data, 0, data.Length) : data;
        var info = new AssetBundleBlockAndDirInfo
        {
            Hash = new Hash128(new byte[16]),
            BlockInfos =
            [
                new AssetBundleBlockInfo
                {
                    DecompressedSize = (uint)data.Length,
                    CompressedSize = (uint)storage.Length,
                    Flags = compress ? (ushort)3 : (ushort)0,
                },
            ],
            DirectoryInfos = [new AssetBundleDirectoryInfo { Name = "source.resS", Offset = 0, DecompressedSize = data.Length, Flags = 0 }],
        };
        using var metadata = new MemoryStream();
        info.Write(new AssetsFileWriter(metadata) { BigEndian = true });
        byte[] infoBytes = metadata.ToArray();
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = 8,
            GenerationVersion = "5.x.x",
            EngineVersion = SyntheticBundles.UNITY_VERSION,
            FileStreamHeader = new AssetBundleFSHeader
            {
                Flags = (AssetBundleFSHeaderFlags)BundleRecipe.CANONICAL_HEADER_FLAGS,
                CompressedSize = (uint)infoBytes.Length,
                DecompressedSize = (uint)infoBytes.Length,
            },
        };
        using var native = new MemoryStream();
        var writer = new AssetsFileWriter(native);
        header.Write(writer);
        writer.Align16();
        writer.Write(infoBytes);
        writer.Align16();
        writer.Write(storage);
        header.FileStreamHeader.TotalFileSize = native.Length;
        native.Position = 0;
        header.Write(writer);
        return native.ToArray();
    }

    private static BundleRecipeHeader Header(AssetBundleHeader header) => new()
    {
        FormatVersion = header.Version,
        GenerationVersion = header.GenerationVersion,
        EngineVersion = header.EngineVersion,
        Flags = (uint)header.FileStreamHeader.Flags,
    };

    private static BundleRecipeEntry Entry(string name, uint flags, long length, params BundleRecipeSegment[] segments) => new()
    {
        Name = name,
        Flags = flags,
        Length = length,
        Segments = segments.ToList(),
    };

    private static BundleRecipeSegment Copy(long offset, long length, int sourceIndex = 0) => new() { Kind = BundleRecipeSegmentKind.Source, SourceIndex = sourceIndex, Offset = offset, Length = length };

    private static BundleRecipeSegment Literal(long offset, long length) => new() { Kind = BundleRecipeSegmentKind.Payload, Offset = offset, Length = length };

    private static byte[] Pattern(int length, int multiplier) => Enumerable.Range(0, length).Select(index => (byte)(index * multiplier)).ToArray();

    // A caller-owned stream that records read requests and can expose short or truncated input.
    private class ProbeStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maxReadResult;
        private readonly long? _reportedLength;

        /// <summary>
        /// Creates an instrumented stream for recipe input and output contracts.
        /// </summary>
        /// <param name="bytes">The initial readable bytes.</param>
        /// <param name="maxReadResult">The maximum bytes returned by each read.</param>
        /// <param name="reportedLength">An optional advertised length for truncation checks.</param>
        /// <param name="canRead">True to advertise readability.</param>
        /// <param name="canSeek">True to advertise seekability.</param>
        /// <param name="canWrite">True to advertise writability.</param>
        public ProbeStream(byte[] bytes, int maxReadResult = int.MaxValue, long? reportedLength = null, bool canRead = true, bool canSeek = true, bool canWrite = true)
        {
            _inner = new MemoryStream();
            _inner.Write(bytes);
            _inner.Position = 0;
            _maxReadResult = maxReadResult;
            _reportedLength = reportedLength;
            CanRead = canRead;
            CanSeek = canSeek;
            CanWrite = canWrite;
        }

        /// <summary>
        /// Gets the largest requested read size.
        /// </summary>
        public int MaxReadRequest { get; private set; }

        /// <summary>
        /// Gets the number of input reads.
        /// </summary>
        public int ReadCalls { get; private set; }

        /// <summary>
        /// Gets whether disposal occurred.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public override bool CanRead { get; }

        /// <inheritdoc />
        public override bool CanSeek { get; }

        /// <inheritdoc />
        public override bool CanWrite { get; }

        /// <inheritdoc />
        public override long Length => _reportedLength ?? _inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            MaxReadRequest = Math.Max(MaxReadRequest, count);
            ReadCalls++;
            return _inner.Read(buffer, offset, Math.Min(count, _maxReadResult));
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override void SetLength(long value) => _inner.SetLength(value);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
