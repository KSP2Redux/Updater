using AssetsTools.NET;
using Ksp2Redux.Tools.BundleConversion.Bundles;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Verifies content-defined source matching, canonicalization, bounded indexes and literal catalogs.
/// </summary>
public class BundleRecipeGenerationTest
{
    /// <summary>
    /// Persists and reconstructs arbitrary complete files without interpreting their signatures or bytes.
    /// </summary>
    /// <param name="length">The independently constructed target file length.</param>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(79)]
    [TestCase(8191)]
    [TestCase(8192)]
    [TestCase(350001)]
    public void RawLiteralFilesPreserveEveryByteIncludingEmptyAndSubMinimumFiles(int length)
    {
        byte[] expected = Noise(length, 123);
        byte[] signature = System.Text.Encoding.UTF8.GetBytes("UnityFS\0invalid\r\n{\"raw\":true}\n\0");
        Array.Copy(signature, expected, Math.Min(signature.Length, expected.Length));
        using var target = new TrackingStream(expected, maxReadResult: 107);
        using var pack = new TrackingStream([]);
        target.Position = 3;
        BundleRecipeGenerationOptions? options = length == 0
            ? new BundleRecipeGenerationOptions { MaxIndexedChunks = 0, MaxRecipeSegments = 0, MaxRangeHints = 0 }
            : null;

        BundleRecipeGenerationResult result = BundleRecipe.GenerateRaw([], [], target, pack, options);

        Assert.That(result.Recipe.Version, Is.EqualTo(2));
        Assert.That(result.Recipe.TargetSha256, Is.EqualTo(Hex.Sha256(expected)));
        Assert.That(result.Recipe.TargetLength, Is.EqualTo(expected.Length));
        Assert.That(result.NativeTargetSha256, Is.EqualTo(Hex.Sha256(expected)));
        Assert.That(result.Metrics.TargetDataBytes, Is.EqualTo(length));
        Assert.That(result.Metrics.LiteralBytes, Is.EqualTo(length));
        Assert.That(result.Metrics.ReusedBytes, Is.Zero);
        Assert.That(pack.Bytes(), Is.EqualTo(expected));
        Assert.That(pack.Position, Is.EqualTo(pack.Length));
        Assert.That(target.Position, Is.EqualTo(3));
        byte[] json = JsonCodec.Write(result.Recipe);
        var persisted = JsonCodec.Read<BundleRecipe>(json)!;
        Assert.That(JsonCodec.Write(persisted), Is.EqualTo(json));
        pack.Position = 7;
        using var output = new TrackingStream([]);

        persisted.Reconstruct([], pack, output);

        Assert.That(output.Bytes(), Is.EqualTo(expected));
        Assert.That(output.Position, Is.EqualTo(length));
        Assert.That(pack.Position, Is.EqualTo(7));
        Assert.That(target.IsDisposed || pack.IsDisposed || output.IsDisposed, Is.False);
        Assert.That(target.MaxReadRequest, Is.LessThanOrEqualTo(131072));
        Assert.That(pack.MaxReadRequest, Is.LessThanOrEqualTo(131072));
        using var nextPack = new MemoryStream();
        Assert.That(JsonCodec.Write(BundleRecipe.GenerateRaw([], [], target, nextPack, options).Recipe), Is.EqualTo(json));
    }

    /// <summary>
    /// Retains the native bytes of a UnityFS file explicitly selected as a raw target.
    /// </summary>
    [Test]
    public void RawProfileDoesNotCanonicalizeEvenAValidUnityFsContainer()
    {
        byte[] expected = SyntheticBundles.Bundle(false, ("native", Noise(300_000, 89), false));
        using var target = new MemoryStream(expected);
        using var pack = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.GenerateRaw([], [], target, pack);
        using var output = new MemoryStream();
        result.Recipe.Reconstruct([], pack, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(result.Recipe.TargetHeader, Is.Null);
        Assert.That(result.Recipe.PackingProfile, Is.EqualTo("raw-file-exact-v1"));
    }

    /// <summary>
    /// Reuses moved large native payloads from raw and decompressed bundle coordinates in one exact file.
    /// </summary>
    [Test]
    public void RawMixedHintsCopyMovedNativePayloadsWithExactAccounting()
    {
        byte[] rawPayload = Noise(350_001, 131);
        byte[] bundledPayload = Noise(420_003, 137);
        byte[] prefix = System.Text.Encoding.ASCII.GetBytes("BKHD\r\nnew metadata\0");
        byte[] gap = Noise(37, 139);
        byte[] suffix = Noise(43, 149);
        byte[] expected = prefix.Concat(bundledPayload).Concat(gap).Concat(rawPayload).Concat(suffix).ToArray();
        using var raw = new TrackingStream(rawPayload, maxReadResult: 113);
        using var bundled = new TrackingStream(SyntheticBundles.Bundle(true,
            ("unrelated", Noise(509, 151), false), ("media", bundledPayload, false)), maxReadResult: 127);
        var descriptors = new[]
        {
            BundleRecipe.DescribeSource(raw, BundleRecipeSourceKind.RawFile),
            BundleRecipe.DescribeSource(bundled, BundleRecipeSourceKind.UnityFsData),
        };
        BundleRecipeRangeHint[] hints =
        [
            new() { TargetEntryIndex = 0, TargetOffset = prefix.Length + bundledPayload.Length + gap.Length,
                SourceIndex = 0, SourceOffset = 0, Length = rawPayload.Length, Sha256 = Hex.Sha256(rawPayload) },
            new() { TargetEntryIndex = 0, TargetOffset = prefix.Length,
                SourceIndex = 1, SourceOffset = 509, Length = bundledPayload.Length, Sha256 = Hex.Sha256(bundledPayload) },
        ];
        using var target = new TrackingStream(expected, maxReadResult: 131);
        using var pack = new TrackingStream([]);
        raw.Position = 5;
        bundled.Position = 7;
        target.Position = 11;

        BundleRecipeGenerationResult result = BundleRecipe.GenerateRaw(descriptors, [raw, bundled], target, pack, rangeHints: hints);
        var recipe = JsonCodec.Read<BundleRecipe>(JsonCodec.Write(result.Recipe))!;
        using var output = new MemoryStream();
        recipe.Reconstruct([raw, bundled], pack, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(result.Metrics.ReusedBytes, Is.EqualTo(rawPayload.Length + bundledPayload.Length));
        Assert.That(result.Metrics.HintedReusedBytes, Is.EqualTo(result.Metrics.ReusedBytes));
        Assert.That(result.Metrics.LiteralBytes, Is.EqualTo(prefix.Length + gap.Length + suffix.Length));
        Assert.That(result.Metrics.ReusedBytes + result.Metrics.LiteralBytes, Is.EqualTo(expected.Length));
        Assert.That(result.Metrics.TargetDataBytes, Is.EqualTo(expected.Length));
        Assert.That(result.Metrics.VerifiedRangeHints, Is.EqualTo(2));
        Assert.That(result.Metrics.OutputSegments, Is.EqualTo(5));
        Assert.That(pack.Bytes(), Is.EqualTo(prefix.Concat(gap).Concat(suffix).ToArray()));
        Assert.That(raw.Position, Is.EqualTo(5));
        Assert.That(bundled.Position, Is.EqualTo(7));
        Assert.That(target.Position, Is.EqualTo(11));
        foreach (var input in new[] { raw, bundled, target, pack })
        {
            Assert.That(input.IsDisposed, Is.False);
            Assert.That(input.MaxReadRequest, Is.LessThanOrEqualTo(131072));
        }
        using var repeatedPack = new MemoryStream();
        Array.Reverse(hints);
        var again = BundleRecipe.GenerateRaw(descriptors, [raw, bundled], target, repeatedPack, rangeHints: hints);
        Assert.That(JsonCodec.Write(again.Recipe), Is.EqualTo(JsonCodec.Write(recipe)));
    }

    /// <summary>
    /// Uses CDC after a native payload moves and selects the first duplicate source deterministically.
    /// </summary>
    [Test]
    public void RawCdcReuseSurvivesShiftAndDuplicateSources()
    {
        byte[] media = Noise(900_000, 157);
        byte[] expected = Noise(131, 163).Concat(media).Concat(Noise(501, 167)).ToArray();
        using var first = new MemoryStream(media.Concat(media).ToArray());
        using var second = new MemoryStream(media);
        var descriptors = new[]
        {
            BundleRecipe.DescribeSource(first, BundleRecipeSourceKind.RawFile),
            BundleRecipe.DescribeSource(second, BundleRecipeSourceKind.RawFile),
        };
        using var target = new MemoryStream(expected);
        using var pack = new MemoryStream();

        var result = BundleRecipe.GenerateRaw(descriptors, [first, second], target, pack);
        using var output = new MemoryStream();
        result.Recipe.Reconstruct([first, second], pack, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(result.Metrics.ReusedBytes, Is.GreaterThan(media.Length * 0.8));
        var copies = result.Recipe.Entries[0].Segments.Where(segment => segment.Kind == BundleRecipeSegmentKind.Source).ToArray();
        Assert.That(copies, Is.Not.Empty);
        Assert.That(copies.All(segment => segment.SourceIndex == 0 && segment.Offset < media.Length), Is.True);
        Assert.That(result.Metrics.ReusedBytes + result.Metrics.LiteralBytes, Is.EqualTo(expected.Length));
    }

    /// <summary>
    /// Deduplicates repeated raw-file chunks while preserving every output occurrence.
    /// </summary>
    [Test]
    public void RawLiteralDeduplicationPreservesRepeatedFileBytes()
    {
        byte[] expected = new byte[600_000];
        using var target = new MemoryStream(expected);
        using var pack = new MemoryStream();
        var result = BundleRecipe.GenerateRaw([], [], target, pack);
        using var output = new MemoryStream();

        result.Recipe.Reconstruct([], pack, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(result.Metrics.DeduplicatedLiteralBytes, Is.GreaterThan(0));
        Assert.That(pack.Length, Is.LessThan(expected.Length));
        Assert.That(result.Metrics.LiteralBytes - result.Metrics.DeduplicatedLiteralBytes, Is.EqualTo(pack.Length));
    }

    /// <summary>
    /// Reuses small complete files and skips CDC indexing for empty or fully hinted raw targets.
    /// </summary>
    /// <param name="length">The raw target length.</param>
    /// <param name="hinted">True to provide a complete checked copy hint.</param>
    [TestCase(0, false)]
    [TestCase(79, false)]
    [TestCase(79, true)]
    [TestCase(300001, true)]
    public void RawEmptyAndCompleteReuseRespectZeroBudgets(int length, bool hinted)
    {
        byte[] expected = Noise(length, 197);
        using var source = new MemoryStream(length == 0 ? Noise(1000, 199) : expected);
        using var target = new MemoryStream(expected);
        using var pack = new MemoryStream();
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        BundleRecipeRangeHint[] hints = hinted
            ? [new BundleRecipeRangeHint { TargetEntryIndex = 0, SourceIndex = 0, Length = length, Sha256 = Hex.Sha256(expected) }]
            : [];
        var options = new BundleRecipeGenerationOptions
        {
            MaxIndexedChunks = hinted || length == 0 ? 0 : 1,
            MaxRecipeSegments = length == 0 ? 0 : 1,
        };

        var result = BundleRecipe.GenerateRaw([descriptor], [source], target, pack, options, hints);
        using var output = new MemoryStream();
        result.Recipe.Reconstruct([source], pack, output);

        Assert.That(output.ToArray(), Is.EqualTo(expected));
        Assert.That(pack.Length, Is.Zero);
        Assert.That(result.Metrics.ReusedBytes, Is.EqualTo(length));
        if (hinted || length == 0)
        {
            Assert.That(result.Metrics.SourceChunksScanned, Is.Zero);
            Assert.That(result.Metrics.IndexRecords, Is.Zero);
        }
    }

    /// <summary>
    /// Rejects actual empty-stream aliasing before raw generation or reconstruction touches storage.
    /// </summary>
    /// <param name="alias">The aliased stream pair.</param>
    [TestCase("generation-source-pack")]
    [TestCase("generation-target-pack")]
    [TestCase("generation-source-target")]
    [TestCase("reconstruction-source-output")]
    [TestCase("reconstruction-source-payload")]
    [TestCase("reconstruction-payload-output")]
    public void RawStreamAliasesAreRejected(string alias)
    {
        using var source = new MemoryStream();
        using var target = new MemoryStream();
        using var pack = new MemoryStream();
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        if (alias.StartsWith("generation-", StringComparison.Ordinal))
        {
            Stream native = alias == "generation-source-target" ? source : target;
            Stream literals = alias switch
            {
                "generation-source-pack" => source,
                "generation-target-pack" => target,
                _ => pack,
            };
            Assert.Throws<ArgumentException>(() => BundleRecipe.GenerateRaw([descriptor], [source], native, literals));
            return;
        }
        var recipe = BundleRecipe.GenerateRaw([descriptor], [source], target, pack).Recipe;
        recipe.Entries[0].Segments.Add(new BundleRecipeSegment { Kind = BundleRecipeSegmentKind.Source, SourceIndex = 0 });
        using var output = new MemoryStream();
        Stream payload = alias == "reconstruction-source-payload" ? source : pack;
        Stream destination = alias switch
        {
            "reconstruction-source-output" => source,
            "reconstruction-payload-output" => payload,
            _ => output,
        };

        Assert.Throws<ArgumentException>(() => recipe.Reconstruct([source], payload, destination));
    }

    /// <summary>
    /// Rejects ambiguous raw/bundle metadata and malformed ranges before any target bytes are written.
    /// </summary>
    /// <param name="damage">The persisted contract defect.</param>
    [TestCase("bundle-version")]
    [TestCase("missing-version")]
    [TestCase("bundle-profile")]
    [TestCase("missing-profile")]
    [TestCase("header")]
    [TestCase("name")]
    [TestCase("flags")]
    [TestCase("extra-entry")]
    [TestCase("missing-entry")]
    [TestCase("null-entry")]
    [TestCase("negative-length")]
    [TestCase("wrong-content-length")]
    [TestCase("overflow-range")]
    [TestCase("overflow-sum")]
    [TestCase("null-segments")]
    [TestCase("literal-source-index")]
    [TestCase("source-index")]
    public void RawSchemaAndRangeConfusionFailsBeforeWriting(string damage)
    {
        using var target = new MemoryStream(new byte[] { 1, 2, 3 });
        using var pack = new MemoryStream();
        var recipe = BundleRecipe.GenerateRaw([], [], target, pack).Recipe;
        var entry = recipe.Entries[0];
        switch (damage)
        {
            case "bundle-version": recipe.Version = 1; break;
            case "missing-version": recipe.Version = 0; break;
            case "bundle-profile": recipe.PackingProfile = BundleRecipe.CANONICAL_PACKING_PROFILE; break;
            case "missing-profile": recipe.PackingProfile = ""; break;
            case "header": recipe.TargetHeader = new BundleRecipeHeader(); break;
            case "name": entry.Name = "file.wem"; break;
            case "flags": entry.Flags = 4; break;
            case "extra-entry": recipe.Entries.Add(new BundleRecipeEntry()); break;
            case "missing-entry": recipe.Entries.Clear(); break;
            case "null-entry": recipe.Entries[0] = null!; break;
            case "negative-length": recipe.TargetLength = entry.Length = -1; break;
            case "wrong-content-length": entry.Length = 2; break;
            case "overflow-range": entry.Segments[0].Offset = long.MaxValue; break;
            case "overflow-sum": entry.Segments.Add(new BundleRecipeSegment { Kind = BundleRecipeSegmentKind.Payload, Length = 3 }); break;
            case "null-segments": entry.Segments = null!; break;
            case "literal-source-index": entry.Segments[0].SourceIndex = 0; break;
            case "source-index": entry.Segments[0].Kind = BundleRecipeSegmentKind.Source; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        var persisted = JsonCodec.Read<BundleRecipe>(JsonCodec.Write(recipe))!;
        using var output = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => persisted.Reconstruct([], pack, output));

        Assert.That(output.Length, Is.Zero);
    }

    /// <summary>
    /// Rejects corrupted sources, literal storage and final identities while restoring caller positions.
    /// </summary>
    /// <param name="damage">The stream or final identity defect.</param>
    [TestCase("source")]
    [TestCase("truncated-source")]
    [TestCase("literal")]
    [TestCase("truncated-literal")]
    [TestCase("hash")]
    [TestCase("output")]
    public void RawCorruptionRestoresPositionsAndNeverReturnsVerifiedOutput(string damage)
    {
        byte[] sourceBytes = Noise(200_000, 173);
        byte[] suffix = Noise(151, 179);
        byte[] expected = sourceBytes.Concat(suffix).ToArray();
        using var source = new TrackingStream(sourceBytes);
        using var target = new MemoryStream(expected);
        using var pack = new TrackingStream([]);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        var hint = new BundleRecipeRangeHint { TargetEntryIndex = 0, SourceIndex = 0,
            Length = sourceBytes.Length, Sha256 = Hex.Sha256(sourceBytes) };
        var result = BundleRecipe.GenerateRaw([descriptor], [source], target, pack, rangeHints: [hint]);
        var recipe = JsonCodec.Read<BundleRecipe>(JsonCodec.Write(result.Recipe))!;
        byte[] literal = pack.Bytes();
        byte[] copiedSource = sourceBytes.ToArray();
        if (damage == "source")
        {
            copiedSource[0] ^= 1;
        }
        if (damage == "literal")
        {
            literal[0] ^= 1;
        }
        if (damage == "hash")
        {
            recipe.TargetSha256 = new string('0', 64);
        }
        using var suppliedSource = new TrackingStream(damage == "truncated-source" ? copiedSource[..^1] : copiedSource,
            reportedLength: copiedSource.Length);
        using var suppliedPack = new TrackingStream(damage == "truncated-literal" ? literal[..^1] : literal,
            reportedLength: literal.Length);
        using var output = new TrackingStream([]) { DamageWrites = damage == "output" };
        suppliedSource.Position = 5;
        suppliedPack.Position = 7;

        if (damage.StartsWith("truncated-", StringComparison.Ordinal))
        {
            Assert.Throws<EndOfStreamException>(() => recipe.Reconstruct([suppliedSource], suppliedPack, output));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => recipe.Reconstruct([suppliedSource], suppliedPack, output));
        }

        Assert.That(suppliedSource.Position, Is.EqualTo(5));
        Assert.That(suppliedPack.Position, Is.EqualTo(7));
        Assert.That(suppliedSource.IsDisposed || suppliedPack.IsDisposed || output.IsDisposed, Is.False);
        if (damage is "source" or "truncated-source")
        {
            Assert.That(output.Length, Is.Zero);
        }
        else
        {
            Assert.That(output.Length, Is.GreaterThan(0));
        }
    }

    /// <summary>
    /// Enforces raw generation aliases and restores inputs when budgets or input mutations cause failure.
    /// </summary>
    /// <param name="failure">The generation failure to exercise.</param>
    [TestCase("alias-target-pack")]
    [TestCase("alias-source-target")]
    [TestCase("alias-source-pack")]
    [TestCase("index-budget")]
    [TestCase("segment-budget")]
    [TestCase("source-mutation")]
    [TestCase("target-mutation")]
    [TestCase("pack-damage")]
    [TestCase("hint-entry")]
    [TestCase("hint-hash")]
    public void RawGenerationFailuresPreserveCallerState(string failure)
    {
        using var source = new TrackingStream(Noise(1000, 181));
        using var target = new TrackingStream(Noise(1001, 191));
        using var pack = new TrackingStream([]);
        var descriptors = new[] { BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile) };
        var options = new BundleRecipeGenerationOptions();
        BundleRecipeRangeHint[] hints = [];
        if (failure == "index-budget")
        {
            options.MaxIndexedChunks = 0;
        }
        if (failure == "segment-budget")
        {
            options.MaxRecipeSegments = 0;
        }
        if (failure == "source-mutation")
        {
            pack.OnFirstWrite = () => source.ChangeByte(0);
        }
        if (failure == "target-mutation")
        {
            pack.OnFirstWrite = () => target.ChangeByte(0);
        }
        if (failure == "pack-damage")
        {
            pack.DamageWrites = true;
        }
        if (failure.StartsWith("hint-", StringComparison.Ordinal))
        {
            hints = [new BundleRecipeRangeHint { TargetEntryIndex = failure == "hint-entry" ? 1 : 0,
                SourceIndex = 0, Length = 10, Sha256 = new string('0', 64) }];
        }
        source.Position = 5;
        target.Position = 7;
        Stream targetArgument = failure == "alias-source-target" ? source : target;
        Stream packArgument = failure switch
        {
            "alias-target-pack" => target,
            "alias-source-pack" => source,
            _ => pack,
        };
        Type expectedException = failure.StartsWith("alias-", StringComparison.Ordinal) ? typeof(ArgumentException)
            : failure.EndsWith("-budget", StringComparison.Ordinal) ? typeof(InvalidOperationException) : typeof(InvalidDataException);

        Assert.Throws(expectedException, () => BundleRecipe.GenerateRaw(descriptors, [source], targetArgument, packArgument, options, hints));

        Assert.That(source.Position, Is.EqualTo(5));
        Assert.That(target.Position, Is.EqualTo(7));
        Assert.That(source.IsDisposed || target.IsDisposed || pack.IsDisposed, Is.False);
    }

    /// <summary>
    /// Reuses shifted and reordered entry bytes from two bundles and a raw file independently of names.
    /// </summary>
    [Test]
    public void ShiftedReorderedMultiSourceContentProducesExactCanonicalRecipe()
    {
        byte[] first = Noise(900_000, 7);
        byte[] second = Noise(650_000, 13);
        byte[] raw = Noise(500_000, 17);
        byte[] firstBundle = SyntheticBundles.Bundle(true, ("unrelated", Noise(5000, 23), false), ("first-source", first, true));
        byte[] secondBundle = SyntheticBundles.Bundle(false, ("second-source", second, false));
        byte[] shifted = Noise(137, 31).Concat(raw).ToArray();
        byte[] appended = first.Concat(Noise(12_319, 37)).ToArray();
        byte[] target = SyntheticBundles.Bundle(false, ("renamed-raw", shifted, false), ("renamed-second", second, true), ("renamed-first", appended, false));
        byte[] expected = SyntheticBundles.Bundle(true, ("renamed-raw", shifted, false), ("renamed-second", second, true), ("renamed-first", appended, false));
        using var source0 = new TrackingStream(firstBundle, maxReadResult: 113);
        using var source1 = new TrackingStream(secondBundle, maxReadResult: 127);
        using var source2 = new TrackingStream(raw, maxReadResult: 139);
        Stream[] sources = [source0, source1, source2];
        BundleRecipeSource[] descriptors =
        [
            BundleRecipe.DescribeSource(source0, BundleRecipeSourceKind.UnityFsData),
            BundleRecipe.DescribeSource(source1, BundleRecipeSourceKind.UnityFsData),
            BundleRecipe.DescribeSource(source2, BundleRecipeSourceKind.RawFile),
        ];
        using var native = new TrackingStream(target, maxReadResult: 151);
        using var pack = new TrackingStream([]);
        using var canonical = new TrackingStream([]);
        source0.Position = 5;
        source1.Position = 7;
        source2.Position = 11;
        native.Position = 13;

        BundleRecipeGenerationResult result = BundleRecipe.Generate(descriptors, sources, native, pack, canonical);

        Assert.That(canonical.Bytes(), Is.EqualTo(expected));
        Assert.That(result.NativeTargetSha256, Is.EqualTo(Hex.Sha256(target)));
        Assert.That(result.Metrics.ReusedBytes, Is.GreaterThan((first.Length + second.Length + raw.Length) * 0.8));
        Assert.That(result.Metrics.ReusedBytes + result.Metrics.LiteralBytes, Is.EqualTo(shifted.Length + second.Length + appended.Length));
        Assert.That(result.Metrics.TargetDataBytes, Is.EqualTo(shifted.Length + second.Length + appended.Length));
        Assert.That(result.Metrics.IndexRecords, Is.EqualTo(result.Metrics.UniqueSourceChunks + result.LiteralChunks.Count));
        Assert.That(result.Metrics.LiteralPackBytes, Is.EqualTo(pack.Length));
        Assert.That(result.Metrics.ElapsedMilliseconds, Is.GreaterThan(0));
        Assert.That(result.Recipe.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "renamed-raw", "renamed-second", "renamed-first" }));
        Assert.That(result.Recipe.Entries.Select(entry => entry.Flags), Is.EqualTo(new uint[] { 0, 4, 0 }));
        Assert.That(result.Recipe.Entries[0].Segments.Any(segment => segment.Kind == BundleRecipeSegmentKind.Source && segment.SourceIndex == 2), Is.True);
        Assert.That(result.Recipe.Entries[1].Segments.All(segment => segment.Kind == BundleRecipeSegmentKind.Source && segment.SourceIndex == 1), Is.True);
        Assert.That(source0.Position, Is.EqualTo(5));
        Assert.That(source1.Position, Is.EqualTo(7));
        Assert.That(source2.Position, Is.EqualTo(11));
        Assert.That(native.Position, Is.EqualTo(13));
        foreach (var stream in new[] { source0, source1, source2, native, pack, canonical })
        {
            Assert.That(stream.IsDisposed, Is.False);
            Assert.That(stream.MaxReadRequest, Is.LessThanOrEqualTo(131072));
        }
        byte[] persisted = JsonCodec.Write(result.Recipe);
        var restored = JsonCodec.Read<BundleRecipe>(persisted)!;
        Assert.That(JsonCodec.Write(restored), Is.EqualTo(persisted));
        using var reconstructed = new MemoryStream();
        restored.Reconstruct(sources, pack, reconstructed);
        Assert.That(reconstructed.ToArray(), Is.EqualTo(expected));
        using var secondPack = new MemoryStream();
        using var secondCanonical = new MemoryStream();
        BundleRecipeGenerationResult again = BundleRecipe.Generate(descriptors, sources, native, secondPack, secondCanonical);
        Assert.That(JsonCodec.Write(again.Recipe), Is.EqualTo(persisted));
        Assert.That(secondPack.ToArray(), Is.EqualTo(pack.Bytes()));
    }

    /// <summary>
    /// Resets source CDC at an entry boundary and merges adjacent copied chunks into one range.
    /// </summary>
    [Test]
    public void EntryBoundaryResetReusesWholeRenamedEntryAfterUnrelatedPrefix()
    {
        byte[] content = Noise(500_000, 41);
        byte[] sourceBytes = SyntheticBundles.Bundle(true, ("prefix", Noise(5000, 43), false), ("old", content, false));
        byte[] targetBytes = SyntheticBundles.Bundle(false, ("new", content, true));
        using var source = new MemoryStream(sourceBytes);
        using var target = new MemoryStream(targetBytes);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.UnityFsData);
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.Generate([descriptor], [source], target, pack, canonical);

        Assert.That(result.Metrics.ReusedBytes, Is.EqualTo(content.Length));
        Assert.That(pack.Length, Is.Zero);
        Assert.That(result.Recipe.Entries[0].Segments, Has.Count.EqualTo(1));
        Assert.That(result.Recipe.Entries[0].Segments[0].Offset, Is.EqualTo(5000));
        Assert.That(result.Recipe.Entries[0].Segments[0].Length, Is.EqualTo(content.Length));
        Assert.That(result.Metrics.SourceChunksScanned, Is.GreaterThan(2));
    }

    /// <summary>
    /// Selects the lowest source index and then lowest offset even when directory order is reversed.
    /// </summary>
    [Test]
    public void DuplicateSourceTieBreakingUsesIndexThenOffset()
    {
        byte[] data = Noise(20_000, 47);
        byte[] bundleBytes = SyntheticBundles.Bundle(false, ("low", data, false), ("high", data, true));
        using (var editable = new MemoryStream(bundleBytes, writable: true))
        using (var bundle = UnityFsBundle.Open(editable))
        {
            bundle.Directory.Reverse();
            editable.Position = bundle.Header.GetBundleInfoOffset();
            new AssetBundleBlockAndDirInfo
            {
                Hash = new Hash128(new byte[16]),
                BlockInfos = bundle.Blocks,
                DirectoryInfos = bundle.Directory,
            }.Write(new AssetsFileWriter(editable) { BigEndian = true });
        }
        using var source0 = new MemoryStream(bundleBytes);
        using var source1 = new MemoryStream(data);
        using var target = new MemoryStream(SyntheticBundles.Bundle(true, ("renamed", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        var descriptors = new[]
        {
            BundleRecipe.DescribeSource(source0, BundleRecipeSourceKind.UnityFsData),
            BundleRecipe.DescribeSource(source1, BundleRecipeSourceKind.RawFile),
        };

        BundleRecipeGenerationResult result = BundleRecipe.Generate(descriptors, [source0, source1], target, pack, canonical);

        Assert.That(result.Recipe.Entries[0].Segments, Has.Count.EqualTo(1));
        Assert.That(result.Recipe.Entries[0].Segments[0].SourceIndex, Is.Zero);
        Assert.That(result.Recipe.Entries[0].Segments[0].Offset, Is.Zero);
        Assert.That(result.Metrics.ReusedBytes, Is.EqualTo(data.Length));
    }

    /// <summary>
    /// Stores identical new entries once and exports exact chunk hashes and ranges for future pack reuse.
    /// </summary>
    [Test]
    public void RepeatedLiteralEntriesReuseOnePackAndVerifiedCatalog()
    {
        byte[] data = Noise(450_000, 53);
        byte[] targetBytes = SyntheticBundles.Bundle(false, ("one", data, true), ("two", data, false), ("empty", [], false));
        using var target = new MemoryStream(targetBytes);
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.Generate([], [], target, pack, canonical);

        Assert.That(pack.ToArray(), Is.EqualTo(data));
        Assert.That(result.Metrics.LiteralBytes, Is.EqualTo(2 * data.Length));
        Assert.That(result.Metrics.DeduplicatedLiteralBytes, Is.EqualTo(data.Length));
        Assert.That(result.Recipe.Entries[0].Segments, Has.Count.EqualTo(1));
        Assert.That(result.Recipe.Entries[1].Segments, Has.Count.EqualTo(1));
        Assert.That(result.Recipe.Entries[1].Segments[0].Offset, Is.Zero);
        Assert.That(result.Recipe.Entries[2].Segments, Is.Empty);
        long offset = 0;
        foreach (var chunk in result.LiteralChunks)
        {
            Assert.That(chunk.Offset, Is.EqualTo(offset));
            Assert.That(chunk.Length, Is.LessThanOrEqualTo(131072));
            Assert.That(chunk.Sha256, Is.EqualTo(Hex.Sha256(data.AsSpan((int)chunk.Offset, chunk.Length).ToArray())));
            offset += chunk.Length;
        }
        Assert.That(offset, Is.EqualTo(pack.Length));
        using var reconstructed = new MemoryStream();
        result.Recipe.Reconstruct([], pack, reconstructed);
        Assert.That(reconstructed.ToArray(), Is.EqualTo(canonical.ToArray()));
    }

    /// <summary>
    /// Handles empty source files, empty targets and empty directory entries without dummy data.
    /// </summary>
    /// <param name="emptyDirectory">True to omit even empty target directory entries.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void EmptyTargetAndEmptyRawSourceNeedNoChunkRecords(bool emptyDirectory)
    {
        using var source = new MemoryStream();
        using var target = new MemoryStream(emptyDirectory ? SyntheticBundles.Bundle(false) : SyntheticBundles.Bundle(false, ("empty", [], false)));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);

        BundleRecipeGenerationResult result = BundleRecipe.Generate([descriptor], [source], target, pack, canonical,
            new BundleRecipeGenerationOptions { MaxIndexedChunks = 0, MaxRecipeSegments = 0 });

        Assert.That(result.Recipe.Entries.All(entry => entry.Segments.Count == 0), Is.True);
        Assert.That(result.Metrics.IndexRecords, Is.Zero);
        Assert.That(pack.Length, Is.Zero);
        using var reconstructed = new MemoryStream();
        result.Recipe.Reconstruct([source], pack, reconstructed);
        Assert.That(reconstructed.ToArray(), Is.EqualTo(canonical.ToArray()));
    }

    /// <summary>
    /// Rejects tampered native sources before writing either output and preserves input ownership.
    /// </summary>
    /// <param name="kind">The source coordinate kind.</param>
    [TestCase(BundleRecipeSourceKind.RawFile)]
    [TestCase(BundleRecipeSourceKind.UnityFsData)]
    public void NativeSourceTamperingIsRejectedBeforeOutputs(BundleRecipeSourceKind kind)
    {
        byte[] data = Noise(100_000, 59);
        byte[] bytes = kind == BundleRecipeSourceKind.RawFile ? data : SyntheticBundles.Bundle(true, ("source", data, false));
        using var source = new TrackingStream(bytes);
        var descriptor = BundleRecipe.DescribeSource(source, kind);
        source.ChangeByte(0);
        using var target = new TrackingStream(SyntheticBundles.Bundle(true, ("target", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        source.Position = 7;
        target.Position = 11;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical));

        Assert.That(pack.Length, Is.Zero);
        Assert.That(canonical.Length, Is.Zero);
        Assert.That(source.Position, Is.EqualTo(7));
        Assert.That(target.Position, Is.EqualTo(11));
        Assert.That(source.IsDisposed || target.IsDisposed, Is.False);
    }

    /// <summary>
    /// Rejects source changes after hashing through exact candidate verification or final identity checking.
    /// </summary>
    [Test]
    public void SourceMutationDuringMatchingCannotProduceSuccessfulRecipe()
    {
        byte[] data = Noise(100_000, 61);
        using var source = new TrackingStream(data);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        using var target = new MemoryStream(SyntheticBundles.Bundle(true, ("target", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new TrackingStream([]);
        canonical.OnFirstWrite = () => source.ChangeByte(10);
        source.Position = 3;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical));

        Assert.That(source.Position, Is.EqualTo(3));
        Assert.That(source.IsDisposed, Is.False);
        Assert.That(canonical.Length, Is.GreaterThan(0));
    }

    /// <summary>
    /// Rejects damaged candidate reads even when whole-file identity reads still return unchanged bytes.
    /// </summary>
    [Test]
    public void ExactCandidateBytesAreCheckedBeyondTheirIndexedHash()
    {
        byte[] data = Noise(100_000, 63);
        using var source = new TrackingStream(data);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        int nativeHashRequestSize = source.MaxReadRequest;
        using var target = new MemoryStream(SyntheticBundles.Bundle(true, ("target", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new TrackingStream([]);
        canonical.OnFirstWrite = () => source.DamageReadsAbove = nativeHashRequestSize;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical));

        Assert.That(source.Bytes(), Is.EqualTo(data));
        Assert.That(source.Position, Is.Zero);
        Assert.That(source.IsDisposed, Is.False);
    }

    /// <summary>
    /// Reads new literal chunks back before accepting their catalog identities.
    /// </summary>
    [Test]
    public void DamagedLiteralOutputCannotProduceSuccessfulCatalog()
    {
        using var target = new MemoryStream(SyntheticBundles.Bundle(true, ("literal", Noise(100_000, 65), false)));
        using var pack = new TrackingStream([]) { DamageWrites = true };
        using var canonical = new MemoryStream();
        target.Position = 3;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([], [], target, pack, canonical));

        Assert.That(target.Position, Is.EqualTo(3));
        Assert.That(pack.IsDisposed, Is.False);
        Assert.That(pack.Length, Is.GreaterThan(0));
    }

    /// <summary>
    /// Rejects truncated streams even if their expected hash describes the readable prefix.
    /// </summary>
    [Test]
    public void AdvertisedLengthCannotHideTruncatedNativeSource()
    {
        byte[] data = Noise(5000, 67);
        using var source = new TrackingStream(data, reportedLength: data.Length + 1);
        var descriptor = new BundleRecipeSource
        {
            Kind = BundleRecipeSourceKind.RawFile,
            Length = data.Length + 1,
            DataLength = data.Length + 1,
            Sha256 = Hex.Sha256(data),
        };
        using var target = new MemoryStream(SyntheticBundles.Bundle(true));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        Assert.Throws<EndOfStreamException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical));

        Assert.That(source.Position, Is.Zero);
        Assert.That(canonical.Length, Is.Zero);
    }

    /// <summary>
    /// Exhausts source-index, literal-index and segment budgets with clear failures and restored inputs.
    /// </summary>
    /// <param name="budget">The exhausted budget.</param>
    [TestCase("source-index")]
    [TestCase("literal-index")]
    [TestCase("segments")]
    public void BudgetExhaustionFailsClearlyInsteadOfGrowingUnbounded(string budget)
    {
        byte[] data = Noise(400_000, 71);
        using var source = new TrackingStream(data);
        using var target = new TrackingStream(SyntheticBundles.Bundle(false, ("one", data, false), ("two", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        BundleRecipeSource[] descriptors = budget == "source-index" ? [BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile)] : [];
        Stream[] sources = budget == "source-index" ? [source] : [];
        var options = new BundleRecipeGenerationOptions
        {
            MaxIndexedChunks = budget == "segments" ? 1000 : 1,
            MaxRecipeSegments = budget == "segments" ? 1 : 1000,
        };
        source.Position = 3;
        target.Position = 5;

        var exception = Assert.Throws<InvalidOperationException>(() => BundleRecipe.Generate(descriptors, sources, target, pack, canonical, options));

        Assert.That(exception!.Message, Does.Contain("budget").And.Contain("exhausted"));
        Assert.That(source.Position, Is.EqualTo(3));
        Assert.That(target.Position, Is.EqualTo(5));
        Assert.That(source.IsDisposed || target.IsDisposed, Is.False);
        if (budget == "source-index")
        {
            Assert.That(canonical.Length, Is.Zero);
            Assert.That(pack.Length, Is.Zero);
        }
    }

    /// <summary>
    /// Rejects aliased stream objects before writing outputs.
    /// </summary>
    /// <param name="alias">The aliased input/output relationship.</param>
    [TestCase("source-source")]
    [TestCase("source-target")]
    [TestCase("source-pack")]
    [TestCase("target-canonical")]
    [TestCase("pack-canonical")]
    public void StreamAliasingIsRejectedBeforeGeneration(string alias)
    {
        using var source = new MemoryStream();
        using var target = new MemoryStream(SyntheticBundles.Bundle(true));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        Stream[] sources = alias == "source-source" ? [source, source] : [source];
        var descriptors = sources.Select(input => BundleRecipe.DescribeSource(input, BundleRecipeSourceKind.RawFile)).ToArray();
        Stream targetArgument = alias == "source-target" ? source : target;
        Stream packArgument = alias == "source-pack" ? source : pack;
        Stream canonicalArgument = alias switch
        {
            "target-canonical" => target,
            "pack-canonical" => pack,
            _ => canonical,
        };

        Assert.Throws<ArgumentException>(() => BundleRecipe.Generate(descriptors, sources, targetArgument, packArgument, canonicalArgument));

        Assert.That(pack.Length, Is.Zero);
        Assert.That(canonical.Length, Is.Zero);
        Assert.That(source.CanRead && target.CanRead, Is.True);
    }

    /// <summary>
    /// Pins generated recipe bytes so CDC boundary changes require explicit profile handling.
    /// </summary>
    [Test]
    public void CdcProfileProducesPinnedPersistedRecipeHash()
    {
        byte[] data = Noise(350_000, 73);
        using var source = new MemoryStream(data);
        using var target = new MemoryStream(SyntheticBundles.Bundle(false, ("shifted", new byte[] { 1, 2, 3 }.Concat(data).ToArray(), false)));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);

        BundleRecipeGenerationResult result = BundleRecipe.Generate([descriptor], [source], target, pack, canonical);

        Assert.That(Hex.Sha256(JsonCodec.Write(result.Recipe)), Is.EqualTo("ba8b3713b36fc83ec35e93d85b7d52176281989170af6792789fe38dcd51464a"));
    }

    /// <summary>
    /// Canonicalizes compressible target bytes while preserving version strings, entry flags and order.
    /// </summary>
    [Test]
    public void CanonicalizationIsExplicitAndPreservesLogicalHeaderAndDirectory()
    {
        byte[] data = Enumerable.Range(0, 200_000).Select(index => (byte)(index * 7)).ToArray();
        using var target = new MemoryStream();
        using var content = new MemoryStream(data);
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = 7,
            GenerationVersion = "target-generation",
            EngineVersion = "6000.6.0f1",
        };
        BundleRewriter.WriteBundle(target, header,
            [new KeyValuePair<AssetBundleDirectoryInfo, Stream>(new AssetBundleDirectoryInfo { Name = "custom.resS", Flags = 0x100 }, content)], false);
        string nativeHash = Hex.Sha256(target.ToArray());
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.Generate([], [], target, pack, canonical);

        Assert.That(result.NativeTargetSha256, Is.EqualTo(nativeHash));
        Assert.That(result.Recipe.TargetSha256, Is.Not.EqualTo(nativeHash));
        Assert.That(result.Recipe.TargetHeader!.FormatVersion, Is.EqualTo(7));
        Assert.That(result.Recipe.TargetHeader.GenerationVersion, Is.EqualTo("target-generation"));
        Assert.That(result.Recipe.TargetHeader.EngineVersion, Is.EqualTo("6000.6.0f1"));
        Assert.That(result.Recipe.Entries[0].Flags, Is.EqualTo(0x100));
        Assert.That(result.Recipe.Entries[0].Name, Is.EqualTo("custom.resS"));
        using var reconstructed = new MemoryStream();
        result.Recipe.Reconstruct([], pack, reconstructed);
        Assert.That(reconstructed.ToArray(), Is.EqualTo(canonical.ToArray()));
    }

    /// <summary>
    /// Rejects invalid target directory ranges before canonical or literal bytes are written.
    /// </summary>
    /// <param name="fault">The malformed target range.</param>
    [TestCase("negative-offset")]
    [TestCase("negative-length")]
    [TestCase("overflow-offset")]
    [TestCase("overflow-length")]
    public void InvalidTargetRangesAreRejectedBeforeWriting(string fault)
    {
        byte[] bytes = SyntheticBundles.Bundle(false, ("target", Noise(5000, 79), false));
        using (var editable = new MemoryStream(bytes, writable: true))
        using (var bundle = UnityFsBundle.Open(editable))
        {
            var entry = bundle.Directory[0];
            switch (fault)
            {
                case "negative-offset": entry.Offset = -1; break;
                case "negative-length": entry.DecompressedSize = -1; break;
                case "overflow-offset": entry.Offset = long.MaxValue; break;
                case "overflow-length": entry.Offset = 1; entry.DecompressedSize = long.MaxValue; break;
                default: throw new ArgumentException("Unknown range fault.", nameof(fault));
            }
            editable.Position = bundle.Header.GetBundleInfoOffset();
            new AssetBundleBlockAndDirInfo
            {
                Hash = new Hash128(new byte[16]),
                BlockInfos = bundle.Blocks,
                DirectoryInfos = bundle.Directory,
            }.Write(new AssetsFileWriter(editable) { BigEndian = true });
        }
        using var target = new TrackingStream(bytes);
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        target.Position = 3;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([], [], target, pack, canonical));

        Assert.That(target.Position, Is.EqualTo(3));
        Assert.That(target.IsDisposed, Is.False);
        Assert.That(pack.Length, Is.Zero);
        Assert.That(canonical.Length, Is.Zero);
    }

    /// <summary>
    /// Reuses a repetitive resource payload moved from a separate entry into an inline serialized entry.
    /// </summary>
    [Test]
    public void VerifiedPayloadHintRecoversMovedRepetitiveBytesThatCdcMisses()
    {
        byte[] payload = Enumerable.Range(0, 450_000).Select(index => (byte)(index / 32768)).ToArray();
        byte[] sourcePrefix = Noise(87, 83);
        byte[] targetPrefix = Noise(113, 89);
        byte[] targetSuffix = Noise(71, 97);
        byte[] sourceBytes = SyntheticBundles.Bundle(true, ("old-cab", sourcePrefix, true), ("separate.resS", payload, false));
        byte[] inline = targetPrefix.Concat(payload).Concat(targetSuffix).ToArray();
        byte[] targetBytes = SyntheticBundles.Bundle(false, ("inline-cab", inline, true));
        using var source = new TrackingStream(sourceBytes, maxReadResult: 127);
        using var target = new TrackingStream(targetBytes, maxReadResult: 131);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.UnityFsData);
        using var noHintPack = new MemoryStream();
        using var noHintCanonical = new MemoryStream();
        BundleRecipeGenerationResult baseline = BundleRecipe.Generate([descriptor], [source], target, noHintPack, noHintCanonical);
        Assert.That(baseline.Metrics.ReusedBytes, Is.Zero);
        var hint = Hint(0, targetPrefix.Length, 0, sourcePrefix.Length, payload);
        hint.Sha256 = hint.Sha256.ToUpperInvariant();
        using var pack = new TrackingStream([]);
        using var canonical = new TrackingStream([]);
        source.Position = 5;
        target.Position = 7;

        BundleRecipeGenerationResult result = BundleRecipe.Generate([descriptor], [source], target, pack, canonical, rangeHints: [hint]);

        Assert.That(result.Metrics.HintedReusedBytes, Is.EqualTo(payload.Length));
        Assert.That(result.Metrics.CdcReusedBytes, Is.Zero);
        Assert.That(result.Metrics.ReusedBytes, Is.EqualTo(payload.Length));
        Assert.That(result.Metrics.LiteralBytes, Is.EqualTo(targetPrefix.Length + targetSuffix.Length));
        Assert.That(pack.Length, Is.EqualTo(targetPrefix.Length + targetSuffix.Length));
        Assert.That(result.Metrics.TargetDataBytes, Is.EqualTo(inline.Length));
        Assert.That(result.Metrics.VerifiedRangeHints, Is.EqualTo(1));
        Assert.That(result.Recipe.MatchingProfile, Is.EqualTo(BundleRecipe.HINTED_MATCHING_PROFILE));
        Assert.That(result.Recipe.TargetSha256, Is.EqualTo(baseline.Recipe.TargetSha256));
        Assert.That(result.Recipe.Entries[0].Segments, Has.Count.EqualTo(3));
        Assert.That(result.Recipe.Entries[0].Segments[1].Kind, Is.EqualTo(BundleRecipeSegmentKind.Source));
        Assert.That(result.Recipe.Entries[0].Segments[1].Offset, Is.EqualTo(sourcePrefix.Length));
        Assert.That(result.Recipe.Entries[0].Segments[1].Length, Is.EqualTo(payload.Length));
        Assert.That(source.Position, Is.EqualTo(5));
        Assert.That(target.Position, Is.EqualTo(7));
        foreach (var stream in new[] { source, target, pack, canonical })
        {
            Assert.That(stream.IsDisposed, Is.False);
            Assert.That(stream.MaxReadRequest, Is.LessThanOrEqualTo(131072));
        }
        using var reconstructed = new MemoryStream();
        result.Recipe.Reconstruct([source], pack, reconstructed);
        Assert.That(reconstructed.ToArray(), Is.EqualTo(canonical.Bytes()));
    }

    /// <summary>
    /// Matches CDC gaps around hints from multiple sources and ignores supplied hint ordering.
    /// </summary>
    [Test]
    public void HintsAndCdcGapsProduceDeterministicMultiSourceRecipe()
    {
        byte[] payload = Noise(350_000, 101);
        byte[] gap = Noise(20_000, 103);
        byte[] edge = Noise(180_000, 107);
        using var source0 = new MemoryStream(payload);
        using var source1 = new MemoryStream(gap);
        using var source2 = new MemoryStream(edge);
        Stream[] sources = [source0, source1, source2];
        var descriptors = sources.Select(source => BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile)).ToArray();
        using var target = new MemoryStream(SyntheticBundles.Bundle(false,
            ("gaps-and-inline", gap.Concat(payload).Concat(gap).ToArray(), true), ("edge", edge, false)));
        BundleRecipeRangeHint[] hints = [Hint(1, 0, 2, 0, edge), Hint(0, gap.Length, 0, 0, payload)];
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.Generate(descriptors, sources, target, pack, canonical, rangeHints: hints);

        Assert.That(pack.Length, Is.Zero);
        Assert.That(result.Metrics.HintedReusedBytes, Is.EqualTo(payload.Length + edge.Length));
        Assert.That(result.Metrics.CdcReusedBytes, Is.EqualTo(2 * gap.Length));
        Assert.That(result.Metrics.ReusedBytes, Is.EqualTo(result.Metrics.TargetDataBytes));
        Assert.That(result.Metrics.VerifiedRangeHints, Is.EqualTo(2));
        Assert.That(result.Recipe.Entries[0].Segments.Select(segment => segment.SourceIndex), Is.EqualTo(new[] { 1, 0, 1 }));
        Assert.That(result.Recipe.Entries[1].Segments[0].SourceIndex, Is.EqualTo(2));
        byte[] persisted = JsonCodec.Write(result.Recipe);
        var restored = JsonCodec.Read<BundleRecipe>(persisted)!;
        Assert.That(JsonCodec.Write(restored), Is.EqualTo(persisted));
        using var reconstructed = new MemoryStream();
        restored.Reconstruct(sources, pack, reconstructed);
        Assert.That(reconstructed.ToArray(), Is.EqualTo(canonical.ToArray()));
        using var otherPack = new MemoryStream();
        using var otherCanonical = new MemoryStream();
        BundleRecipeGenerationResult reordered = BundleRecipe.Generate(descriptors, sources, target, otherPack, otherCanonical,
            rangeHints: hints.Reverse().ToArray());
        Assert.That(JsonCodec.Write(reordered.Recipe), Is.EqualTo(persisted));
        Assert.That(otherCanonical.ToArray(), Is.EqualTo(canonical.ToArray()));
    }

    /// <summary>
    /// Merges adjacent verified ranges and avoids building an unused CDC index for fully hinted content.
    /// </summary>
    [Test]
    public void AdjacentEdgeHintsMergeUnderOneSegmentBudget()
    {
        byte[] data = Noise(350_000, 109);
        const int split = 150_001;
        using var source = new TrackingStream(data, maxReadResult: 137);
        using var target = new TrackingStream(SyntheticBundles.Bundle(false, ("whole", data, false)), maxReadResult: 139);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        BundleRecipeRangeHint[] hints =
        [
            Hint(0, split, 0, split, data[split..]),
            Hint(0, 0, 0, 0, data[..split]),
        ];
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.Generate([descriptor], [source], target, pack, canonical,
            new BundleRecipeGenerationOptions { MaxIndexedChunks = 0, MaxRecipeSegments = 1, MaxRangeHints = 2 }, hints);

        Assert.That(result.Recipe.Entries[0].Segments, Has.Count.EqualTo(1));
        Assert.That(result.Recipe.Entries[0].Segments[0].Offset, Is.Zero);
        Assert.That(result.Recipe.Entries[0].Segments[0].Length, Is.EqualTo(data.Length));
        Assert.That(result.Metrics.HintedReusedBytes, Is.EqualTo(data.Length));
        Assert.That(result.Metrics.SourceChunksScanned, Is.Zero);
        Assert.That(result.Metrics.TargetChunksScanned, Is.Zero);
        Assert.That(result.Metrics.IndexRecords, Is.Zero);
        Assert.That(result.Metrics.VerifiedRangeHints, Is.EqualTo(2));
        Assert.That(pack.Length, Is.Zero);
        Assert.That(source.MaxReadRequest, Is.LessThanOrEqualTo(65536));
        Assert.That(target.MaxReadRequest, Is.LessThanOrEqualTo(131072));
    }

    /// <summary>
    /// Keeps the original CDC recipe and profile byte-identical when hints are omitted or empty.
    /// </summary>
    [Test]
    public void EmptyHintsKeepCdcOutputAndPersistedGoldenStable()
    {
        byte[] data = Noise(350_000, 73);
        using var source = new MemoryStream(data);
        using var target = new MemoryStream(SyntheticBundles.Bundle(false, ("shifted", new byte[] { 1, 2, 3 }.Concat(data).ToArray(), false)));
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        BundleRecipeGenerationResult result = BundleRecipe.Generate([descriptor], [source], target, pack, canonical, rangeHints: []);

        Assert.That(Hex.Sha256(JsonCodec.Write(result.Recipe)), Is.EqualTo("ba8b3713b36fc83ec35e93d85b7d52176281989170af6792789fe38dcd51464a"));
        Assert.That(result.Metrics.HintedReusedBytes, Is.Zero);
        Assert.That(result.Metrics.CdcReusedBytes, Is.EqualTo(result.Metrics.ReusedBytes));
    }

    /// <summary>
    /// Rejects malformed, unknown or overlapping hint metadata and incorrect hash claims before writes.
    /// </summary>
    /// <param name="fault">The invalid hint field or overlap.</param>
    [TestCase("null")]
    [TestCase("target-index-negative")]
    [TestCase("target-index-past-end")]
    [TestCase("source-index-negative")]
    [TestCase("source-index-past-end")]
    [TestCase("target-negative-offset")]
    [TestCase("source-negative-offset")]
    [TestCase("zero-length")]
    [TestCase("negative-length")]
    [TestCase("hash-null")]
    [TestCase("hash-short")]
    [TestCase("hash-nonhex")]
    [TestCase("hash-wrong")]
    [TestCase("target-past-end")]
    [TestCase("source-past-end")]
    [TestCase("target-overflow-offset")]
    [TestCase("source-overflow-offset")]
    [TestCase("overflow-length")]
    [TestCase("duplicate")]
    [TestCase("overlap")]
    public void BrokenHintClaimsDoNotSilentlyFallBackToLiterals(string fault)
    {
        byte[] data = Noise(1000, 113);
        using var source = new TrackingStream(data);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        using var target = new TrackingStream(SyntheticBundles.Bundle(false, ("target", data, false)));
        var hint = Hint(0, 0, 0, 0, data);
        var hints = new List<BundleRecipeRangeHint> { hint };
        switch (fault)
        {
            case "null": hints[0] = null!; break;
            case "target-index-negative": hint.TargetEntryIndex = -1; break;
            case "target-index-past-end": hint.TargetEntryIndex = 1; break;
            case "source-index-negative": hint.SourceIndex = -1; break;
            case "source-index-past-end": hint.SourceIndex = 1; break;
            case "target-negative-offset": hint.TargetOffset = -1; break;
            case "source-negative-offset": hint.SourceOffset = -1; break;
            case "zero-length": hint.Length = 0; break;
            case "negative-length": hint.Length = -1; break;
            case "hash-null": hint.Sha256 = null!; break;
            case "hash-short": hint.Sha256 = "123"; break;
            case "hash-nonhex": hint.Sha256 = new string('g', 64); break;
            case "hash-wrong": hint.Sha256 = new string('0', 64); break;
            case "target-past-end": hint.TargetOffset = data.Length; break;
            case "source-past-end": hint.SourceOffset = data.Length; break;
            case "target-overflow-offset": hint.TargetOffset = long.MaxValue; break;
            case "source-overflow-offset": hint.SourceOffset = long.MaxValue; break;
            case "overflow-length": hint.TargetOffset = 1; hint.Length = long.MaxValue; break;
            case "duplicate": hints.Add(Hint(0, 0, 0, 0, data)); break;
            case "overlap": hints.Add(Hint(0, 500, 0, 500, data[500..])); break;
            default: throw new ArgumentException("Unknown hint fault.", nameof(fault));
        }
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        source.Position = 7;
        target.Position = 11;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical, rangeHints: hints));

        Assert.That(pack.Length, Is.Zero);
        Assert.That(canonical.Length, Is.Zero);
        Assert.That(source.Position, Is.EqualTo(7));
        Assert.That(target.Position, Is.EqualTo(11));
        Assert.That(source.IsDisposed || target.IsDisposed, Is.False);
    }

    /// <summary>
    /// Rejects a valid native source identity whose hinted payload differs from target bytes.
    /// </summary>
    [Test]
    public void HintDoesNotAuthorizeReplacingTargetWithDifferentSourceBytes()
    {
        byte[] data = Noise(100_000, 127);
        byte[] changed = data.ToArray();
        changed[99_999] ^= 0x40;
        using var source = new MemoryStream(changed);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        using var target = new MemoryStream(SyntheticBundles.Bundle(false, ("target", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical,
            rangeHints: [Hint(0, 0, 0, 0, data)]));

        Assert.That(pack.Length, Is.Zero);
        Assert.That(canonical.Length, Is.Zero);
    }

    /// <summary>
    /// Rechecks canonical emission against a previously valid hint when candidate reads become corrupted.
    /// </summary>
    [Test]
    public void HintCandidateIsCheckedAgainAfterCanonicalization()
    {
        byte[] data = Noise(180_000, 131);
        using var source = new TrackingStream(data);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        int nativeHashRequestSize = source.MaxReadRequest;
        using var target = new MemoryStream(SyntheticBundles.Bundle(false, ("target", data, false)));
        using var pack = new MemoryStream();
        using var canonical = new TrackingStream([]);
        canonical.OnFirstWrite = () => source.DamageReadsAbove = nativeHashRequestSize;
        source.Position = 3;

        Assert.Throws<InvalidDataException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical,
            rangeHints: [Hint(0, 0, 0, 0, data)]));

        Assert.That(source.Bytes(), Is.EqualTo(data));
        Assert.That(source.Position, Is.EqualTo(3));
        Assert.That(source.IsDisposed, Is.False);
        Assert.That(canonical.Length, Is.GreaterThan(0));
    }

    /// <summary>
    /// Bounds supplied hint metadata and applies the existing segment budget to hinted output.
    /// </summary>
    /// <param name="budget">The exhausted metadata or segment budget.</param>
    [TestCase("hints")]
    [TestCase("segments")]
    public void HintBudgetsFailClearlyAndRestoreInputs(string budget)
    {
        byte[] data = Noise(20_000, 137);
        using var source = new MemoryStream(data);
        var descriptor = BundleRecipe.DescribeSource(source, BundleRecipeSourceKind.RawFile);
        using var target = new MemoryStream(SyntheticBundles.Bundle(false, ("one", data, false), ("two", data, false)));
        BundleRecipeRangeHint[] hints = [Hint(0, 0, 0, 0, data), Hint(1, 0, 0, 0, data)];
        var options = new BundleRecipeGenerationOptions
        {
            MaxRangeHints = budget == "hints" ? 1 : 2,
            MaxRecipeSegments = budget == "segments" ? 1 : 100,
            MaxIndexedChunks = 0,
        };
        using var pack = new MemoryStream();
        using var canonical = new MemoryStream();
        source.Position = 5;
        target.Position = 7;

        var exception = Assert.Throws<InvalidOperationException>(() => BundleRecipe.Generate([descriptor], [source], target, pack, canonical, options, hints));

        Assert.That(exception!.Message, Does.Contain("budget").And.Contain("exhausted"));
        Assert.That(source.Position, Is.EqualTo(5));
        Assert.That(target.Position, Is.EqualTo(7));
        Assert.That(source.CanRead && target.CanRead, Is.True);
        if (budget == "hints")
        {
            Assert.That(canonical.Length, Is.Zero);
        }
    }

    private static BundleRecipeRangeHint Hint(int entryIndex, long targetOffset, int sourceIndex, long sourceOffset, byte[] bytes) => new()
    {
        TargetEntryIndex = entryIndex,
        TargetOffset = targetOffset,
        SourceIndex = sourceIndex,
        SourceOffset = sourceOffset,
        Length = bytes.Length,
        Sha256 = Hex.Sha256(bytes),
    };

    private static byte[] Noise(int length, uint seed)
    {
        var data = new byte[length];
        uint state = seed;
        for (int index = 0; index < data.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[index] = (byte)state;
        }
        return data;
    }

    // Instrumented caller-owned storage with controllable reads and mutation during generation.
    private class TrackingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly int _maxReadResult;
        private readonly long? _reportedLength;
        private bool _wrote;

        /// <summary>
        /// Creates an instrumented stream.
        /// </summary>
        /// <param name="bytes">The initial bytes.</param>
        /// <param name="maxReadResult">The maximum bytes returned by each read.</param>
        /// <param name="reportedLength">An optional advertised length.</param>
        public TrackingStream(byte[] bytes, int maxReadResult = int.MaxValue, long? reportedLength = null)
        {
            _inner.Write(bytes);
            _inner.Position = 0;
            _maxReadResult = maxReadResult;
            _reportedLength = reportedLength;
        }

        /// <summary>
        /// Gets the largest requested read size.
        /// </summary>
        public int MaxReadRequest { get; private set; }

        /// <summary>
        /// Gets whether this stream was disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Gets or sets an action invoked before the first output write.
        /// </summary>
        public Action? OnFirstWrite { get; set; }

        /// <summary>
        /// Gets or sets the request-size threshold above which returned reads are damaged.
        /// </summary>
        public int DamageReadsAbove { get; set; } = int.MaxValue;

        /// <summary>
        /// Gets or sets whether the first byte of each completed write is damaged in storage.
        /// </summary>
        public bool DamageWrites { get; set; }

        /// <summary>
        /// Returns the stored bytes for synthetic comparisons.
        /// </summary>
        /// <returns>The stored byte copy.</returns>
        public byte[] Bytes() => _inner.ToArray();

        /// <summary>
        /// Mutates a native byte without changing the externally visible position.
        /// </summary>
        /// <param name="offset">The byte offset to mutate.</param>
        public void ChangeByte(long offset)
        {
            long position = _inner.Position;
            _inner.Position = offset;
            int value = _inner.ReadByte();
            _inner.Position = offset;
            _inner.WriteByte((byte)(value ^ 0x40));
            _inner.Position = position;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => true;

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
            int read = _inner.Read(buffer, offset, Math.Min(count, _maxReadResult));
            if (read > 0 && count > DamageReadsAbove)
            {
                buffer[offset] ^= 0x40;
            }
            return read;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override void SetLength(long value) => _inner.SetLength(value);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_wrote)
            {
                _wrote = true;
                OnFirstWrite?.Invoke();
            }
            _inner.Write(buffer, offset, count);
            if (DamageWrites && count > 0)
            {
                ChangeByte(_inner.Position - count);
            }
        }

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
