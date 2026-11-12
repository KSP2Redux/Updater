using System.Diagnostics;
using System.Security.Cryptography;
using AssetsTools.NET;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

public partial class BundleRecipe
{
    /// <summary>
    /// The deterministic CDC profile used by the generator.
    /// </summary>
    /// <remarks>
    /// Gear64 shifts the previous hash left by one bit and adds table[byte] with unsigned wraparound.
    /// SplitMix64 generates
    /// table entries 0 through 255 from seed 0x4F4D4E4950415443. A chunk ends after at least 8192 bytes
    /// when the low 15 hash bits are zero, or at 131072 bytes. The nominal boundary interval is 32 KiB
    /// before minimum and maximum constraints. State resets at each chunk and directory entry.
    /// Chunk identity is SHA-256, followed by exact byte comparison. Changes require a new profile.
    /// </remarks>
    public const string CDC_MATCHING_PROFILE = "gear64-sm64-4f4d4e4950415443-min8k-mask15-max128k-v1";

    /// <summary>
    /// The matching profile for verified payload ranges with CDC restarted at each remaining gap.
    /// </summary>
    public const string HINTED_MATCHING_PROFILE = CDC_MATCHING_PROFILE + "+verified-ranges-v1";

    private const int CDC_MIN_CHUNK_SIZE = 8192;
    private const int CDC_MAX_CHUNK_SIZE = 131072;
    private const int CDC_READ_SIZE = 65536;
    private const ulong CDC_BOUNDARY_MASK = 0x7FFF;
    private static readonly ulong[] GEAR_TABLE = CreateGearTable();

    /// <summary>
    /// Hashes a native file and describes its raw or decompressed UnityFS coordinate space.
    /// </summary>
    /// <remarks>
    /// The stream remains open and its position is restored on success and failure. A bundle's
    /// directory and block ranges are checked, but its entry bytes are not decompressed here.
    /// An expected descriptor from a trusted inventory can be supplied to Generate instead.
    /// </remarks>
    /// <param name="source">The readable, seekable native file stream.</param>
    /// <param name="kind">The coordinate space to expose.</param>
    /// <returns>The native file identity and range metadata.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the input stream or coordinate kind is unsuitable.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the UnityFS metadata contains unsupported or invalid ranges.
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown when the input ends before its advertised length.
    /// </exception>
    public static BundleRecipeSource DescribeSource(Stream source, BundleRecipeSourceKind kind)
    {
        RequireInput(source, nameof(source));
        if (kind is not (BundleRecipeSourceKind.RawFile or BundleRecipeSourceKind.UnityFsData))
            throw new ArgumentOutOfRangeException(nameof(kind));
        long position = source.Position;
        try
        {
            var descriptor = new BundleRecipeSource
            {
                Kind = kind,
                Length = source.Length,
                DataLength = source.Length,
                Sha256 = HashFile(source, source.Length),
            };
            if (kind == BundleRecipeSourceKind.RawFile)
                return descriptor;

            using var bundle = OpenSource(source);
            descriptor.DataLength = bundle.Data.Length;
            descriptor.Header = DescribeHeader(bundle.Header);
            ValidateHeader(descriptor.Header);
            ValidateSource(bundle, descriptor);
            return descriptor;
        }
        finally
        {
            source.Position = position;
        }
    }

    /// <summary>
    /// Canonicalizes a UnityFS target and generates a bounded CDC range recipe and literal pack.
    /// </summary>
    /// <remarks>
    /// All streams must be readable and seekable. Outputs must also be writable, empty and at zero.
    /// Stream objects and output storage must be distinct. All caller streams stay open and input
    /// positions are restored. Successful outputs end at their lengths. Failed outputs may contain
    /// partial or unverified data, and both must be discarded. Inputs and descriptors must remain
    /// unchanged during the operation. Native hashes are verified before indexing and again before
    /// returning. The recipe describes canonical writer bytes, not the original target compressor.
    /// Source CDC resets per directory entry, including resources, or spans the entire raw file.
    /// Duplicate source chunks choose the lowest source index, then lowest coordinate offset.
    /// The literal pack starts empty and deduplicates chunks within this generation only. Its returned
    /// chunk catalog is the reuse boundary for a later shared-pack resolver. No shared storage exists here.
    /// CDC uses a 128 KiB chunk buffer and two 64 KiB read/verification buffers. The chunk-record budget
    /// does not cap total memory. UnityFS readers retain metadata and up to eight native-sized storage
    /// blocks each, and the writer retains target directory/block metadata. No whole-file array is added.
    /// Optional range hints are bounded, copied and sorted by target entry index and relative offset.
    /// Their indices, ranges, hashes and nonoverlap are checked before any output writes. Both range
    /// hashes and exact bytes are verified before acceptance. Hints never replace or alter target data.
    /// CDC resets at the edges of each unhinted gap. Fully hinted content needs no CDC index. Supplied
    /// hints are not stored in the recipe, which already supports the resulting source-copy ranges.
    /// </remarks>
    /// <param name="descriptors">Expected native identities in source-index order.</param>
    /// <param name="sources">Readable, seekable native source streams in descriptor order.</param>
    /// <param name="target">The readable, seekable UnityFS target to canonicalize.</param>
    /// <param name="literalPack">An empty readable, writable, seekable stream for new literal chunks.</param>
    /// <param name="canonicalTarget">An empty readable, writable, seekable stream for canonical target bytes.</param>
    /// <param name="options">Index and recipe-size budgets, or null for the documented defaults.</param>
    /// <param name="rangeHints">Explicit payload reuse claims, or null for unchanged CDC-only matching.</param>
    /// <returns>The persisted recipe, literal chunk catalog, native target identity and measured counters.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when streams, aliasing, initial output state or budgets are unsuitable.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when identities, metadata, chunks or generated ranges do not match their declared bytes.
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown when an input or verification range ends early.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured chunk-record or recipe-segment budget is exhausted.
    /// </exception>
    public static BundleRecipeGenerationResult Generate(
        IReadOnlyList<BundleRecipeSource> descriptors,
        IReadOnlyList<Stream> sources,
        Stream target,
        Stream literalPack,
        Stream canonicalTarget,
        BundleRecipeGenerationOptions? options = null,
        IReadOnlyList<BundleRecipeRangeHint>? rangeHints = null)
    {
        RequireGenerationOutput(canonicalTarget, nameof(canonicalTarget));
        return GenerateCore(descriptors, sources, target, literalPack, canonicalTarget, options, rangeHints);
    }

    /// <summary>
    /// Generates an exact raw-file recipe and literal pack through the shared CDC and range matcher.
    /// </summary>
    /// <remarks>
    /// The complete target bytes are preserved without parsing, compression or normalization, even for
    /// a target that happens to contain a UnityFS signature. Version 2 and the raw-file packing profile
    /// identify this contract. Range hints address content entry zero and native target offsets.
    /// Source descriptors may expose raw or decompressed UnityFS bytes. All sources are verified,
    /// including unused ones. Caller streams remain open and input positions restore on either outcome.
    /// Streams and output storage must be distinct. The literal pack must start empty at zero and ends
    /// at its length on success. Discard it on failure. Inputs and descriptors must remain unchanged.
    /// Matching, tie-breaking, index/segment/hint budgets and literal deduplication are the same as
    /// Generate. Per-read buffers are bounded, but UnityFS metadata and native block caches are not
    /// covered by an overall memory budget. No complete target copy or whole-file array is required.
    /// </remarks>
    /// <param name="descriptors">Expected native identities in source-index order.</param>
    /// <param name="sources">Readable, seekable native source streams in descriptor order.</param>
    /// <param name="target">The readable, seekable complete target file, including an empty file.</param>
    /// <param name="literalPack">An empty readable, writable, seekable stream for new literal chunks.</param>
    /// <param name="options">Index and recipe-size budgets, or null for the documented defaults.</param>
    /// <param name="rangeHints">Verified entry-zero/native-target reuse claims, or null for CDC alone.</param>
    /// <returns>The persisted exact-file recipe, literal chunk catalog, target identity and counters.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when descriptors, sources, an input stream or the literal pack is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when streams, aliasing, initial output state or budgets are unsuitable.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when identities, metadata, chunks or hints do not match their declared bytes.
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown when an input or verification range ends early.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured chunk-record, segment or range-hint budget is exhausted.
    /// </exception>
    public static BundleRecipeGenerationResult GenerateRaw(
        IReadOnlyList<BundleRecipeSource> descriptors,
        IReadOnlyList<Stream> sources,
        Stream target,
        Stream literalPack,
        BundleRecipeGenerationOptions? options = null,
        IReadOnlyList<BundleRecipeRangeHint>? rangeHints = null)
        => GenerateCore(descriptors, sources, target, literalPack, null, options, rangeHints);

    private static BundleRecipeGenerationResult GenerateCore(
        IReadOnlyList<BundleRecipeSource> descriptors,
        IReadOnlyList<Stream> sources,
        Stream target,
        Stream literalPack,
        Stream? canonicalTarget,
        BundleRecipeGenerationOptions? options,
        IReadOnlyList<BundleRecipeRangeHint>? rangeHints,
        ReleaseSourceIndex? releaseSources = null,
        (long Length, string Sha256)? expectedNativeTarget = null)
    {
        if (descriptors is null)
            throw new ArgumentNullException(nameof(descriptors));
        if (sources is null)
            throw new ArgumentNullException(nameof(sources));
        RequireInput(target, nameof(target));
        RequireGenerationOutput(literalPack, nameof(literalPack));
        bool isRaw = canonicalTarget is null;
        options ??= new BundleRecipeGenerationOptions();
        if (options.MaxIndexedChunks < 0 || options.MaxRecipeSegments < 0 || options.MaxRangeHints < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Generation budgets cannot be negative.");
        if (descriptors.Count != sources.Count)
            throw new ArgumentException("Source descriptors and stream slots must have the same count.");

        var inputs = new List<Stream>(sources.Count + 1) { target };
        foreach (var source in sources)
        {
            RequireInput(source, nameof(sources));
            inputs.Add(source);
        }
        var distinct = new List<Stream>(inputs) { literalPack };
        if (canonicalTarget is not null)
        {
            distinct.Add(canonicalTarget);
        }
        for (int index = 0; index < distinct.Count; index++)
        {
            for (int previous = 0; previous < index; previous++)
            {
                if (ReferenceEquals(distinct[index], distinct[previous]))
                    throw new ArgumentException("Generation streams must be distinct objects.");
            }
        }

        var positions = new long[inputs.Count];
        for (int index = 0; index < inputs.Count; index++)
        {
            positions[index] = inputs[index].Position;
        }
        var opened = new List<UnityFsBundle>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            CancellationToken cancellationToken = releaseSources?.CancellationToken ?? default;
            long nativeTargetLength = target.Length;
            if (expectedNativeTarget.HasValue && nativeTargetLength != expectedNativeTarget.Value.Length)
                throw new InvalidDataException("The native target length differs from the frozen target contract.");
            string nativeTargetHash = HashFile(target, nativeTargetLength, cancellationToken);
            if (expectedNativeTarget is { } pin && !string.Equals(nativeTargetHash, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The native target digest differs from the frozen target contract.");
            UnityFsBundle? targetBundle = null;
            Stream targetData = new BoundedInputStream(target, cancellationToken);
            var targetDirectory = new List<AssetBundleDirectoryInfo>
            {
                new() { Offset = 0, DecompressedSize = nativeTargetLength, Name = "", Flags = 0 },
            };
            if (!isRaw)
            {
                targetBundle = OpenSource(target);
                opened.Add(targetBundle);
                var targetDescriptor = new BundleRecipeSource
                {
                    Kind = BundleRecipeSourceKind.UnityFsData,
                    Length = nativeTargetLength,
                    DataLength = targetBundle.Data.Length,
                    Sha256 = nativeTargetHash,
                    Header = DescribeHeader(targetBundle.Header),
                };
                ValidateHeader(targetDescriptor.Header);
                ValidateSource(targetBundle, targetDescriptor);
                targetData = new BoundedInputStream(targetBundle.Data, cancellationToken);
                targetDirectory = targetBundle.Directory;
                ValidateGenerationDirectory(targetDirectory);
            }

            var recipe = new BundleRecipe
            {
                Version = isRaw ? RAW_TARGET_VERSION : CURRENT_VERSION,
                PackingProfile = isRaw ? RAW_PACKING_PROFILE : CANONICAL_PACKING_PROFILE,
                MatchingProfile = CDC_MATCHING_PROFILE,
                TargetHeader = isRaw ? null : DescribeHeader(targetBundle!.Header),
                TargetLength = isRaw ? 0 : 1,
                TargetSha256 = new string('0', 64),
            };
            if (isRaw)
            {
                // Initial validation needs a complete content shape before any source is opened.
                recipe.Entries.Add(new BundleRecipeEntry());
            }
            else
            {
                recipe.TargetHeader!.Flags = CANONICAL_HEADER_FLAGS;
            }
            foreach (var descriptor in descriptors)
            {
                if (descriptor is null)
                    throw new InvalidDataException("A source descriptor is null.");
                recipe.Sources.Add(CloneSource(descriptor));
            }
            recipe.Validate(sources.Count, 0);
            recipe.Entries.Clear();
            List<BundleRecipeRangeHint> hints = ValidateRangeHints(rangeHints, targetDirectory, recipe.Sources, options.MaxRangeHints);
            if (hints.Count > 0)
            {
                recipe.MatchingProfile = HINTED_MATCHING_PROFILE;
            }

            var (data, directories) = releaseSources is null
                ? OpenGenerationSources(recipe.Sources, sources, opened)
                : (releaseSources.Data, releaseSources.Directories);

            using var state = new GenerationState(data, literalPack, options);
            foreach (var hint in hints)
            {
                state.VerifyHint(targetData,
                    checked(targetDirectory[hint.TargetEntryIndex].Offset + hint.TargetOffset), hint);
            }
            bool needsCdc = (hints.Count == 0 && !isRaw) || HasUnhintedContent(targetDirectory, hints);
            if (needsCdc)
            {
                if (releaseSources is null)
                    state.IndexSources(directories, default);
                else
                {
                    state.SourceChunks = releaseSources.GetChunks();
                }
            }

            if (!isRaw)
            {
                var entries = new List<KeyValuePair<AssetBundleDirectoryInfo, Stream>>(targetDirectory.Count);
                foreach (var entry in targetDirectory)
                {
                    entries.Add(new KeyValuePair<AssetBundleDirectoryInfo, Stream>(entry,
                        new SegmentStream(targetData, entry.Offset, entry.DecompressedSize)));
                }
                BundleRewriter.WriteBundle(canonicalTarget!, targetBundle!.Header, entries, compress: true);
                recipe.TargetLength = canonicalTarget!.Length;
                recipe.TargetSha256 = HashFile(canonicalTarget, recipe.TargetLength, cancellationToken);

                UnityFsBundle canonical = OpenSource(canonicalTarget);
                opened.Add(canonical);
                targetData = new BoundedInputStream(canonical.Data, cancellationToken);
                targetDirectory = canonical.Directory;
            }
            else
            {
                recipe.TargetLength = nativeTargetLength;
                recipe.TargetSha256 = nativeTargetHash;
            }
            int hintIndex = 0;
            for (int entryIndex = 0; entryIndex < targetDirectory.Count; entryIndex++)
            {
                var entry = targetDirectory[entryIndex];
                var planned = new BundleRecipeEntry
                {
                    Name = entry.Name,
                    Flags = entry.Flags,
                    Length = entry.DecompressedSize,
                };
                recipe.Entries.Add(planned);
                long cursor = 0;
                while (hintIndex < hints.Count && hints[hintIndex].TargetEntryIndex == entryIndex)
                {
                    var hint = hints[hintIndex++];
                    state.WalkChunks(targetData, checked(entry.Offset + cursor), hint.TargetOffset - cursor,
                        (_, bytes, length) => state.MatchTarget(planned, bytes, length));
                    // Recheck against the output profile's coordinate stream before emitting the range.
                    state.VerifyHint(targetData, checked(entry.Offset + hint.TargetOffset), hint);
                    state.AcceptHint(planned, hint);
                    cursor = checked(hint.TargetOffset + hint.Length);
                }
                state.WalkChunks(targetData, checked(entry.Offset + cursor), entry.DecompressedSize - cursor,
                    (_, bytes, length) => state.MatchTarget(planned, bytes, length));
            }
            recipe.PayloadLength = literalPack.Length;
            recipe.Validate(sources.Count, literalPack.Length);
            if (releaseSources is null)
            {
                for (int index = 0; index < sources.Count; index++)
                {
                    VerifyNativeIdentity(sources[index], recipe.Sources[index]);
                }
            }
            if (target.Length != nativeTargetLength || HashFile(target, nativeTargetLength, cancellationToken) != nativeTargetHash)
                throw new InvalidDataException("The native target changed during generation.");
            if (canonicalTarget is not null && HashFile(canonicalTarget, recipe.TargetLength, cancellationToken) != recipe.TargetSha256)
                throw new InvalidDataException("The canonical target changed during generation.");
            literalPack.Flush();
            literalPack.Position = literalPack.Length;
            if (canonicalTarget is not null)
            {
                canonicalTarget.Position = canonicalTarget.Length;
            }
            stopwatch.Stop();
            state.Metrics.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            state.Metrics.LiteralPackBytes = literalPack.Length;
            return new BundleRecipeGenerationResult(recipe, nativeTargetHash, nativeTargetLength, state.Metrics, state.Literals);
        }
        finally
        {
            foreach (var bundle in opened)
            {
                bundle.Dispose();
            }
            for (int index = 0; index < inputs.Count; index++)
            {
                inputs[index].Position = positions[index];
            }
        }
    }

    private static (Stream[], List<AssetBundleDirectoryInfo>?[]) OpenGenerationSources(
        IReadOnlyList<BundleRecipeSource> descriptors, IReadOnlyList<Stream> sources, List<UnityFsBundle> opened)
    {
        var data = new Stream[sources.Count];
        var directories = new List<AssetBundleDirectoryInfo>?[sources.Count];
        for (int index = 0; index < sources.Count; index++)
        {
            var descriptor = descriptors[index];
            VerifyNativeIdentity(sources[index], descriptor);
            if (descriptor.Kind == BundleRecipeSourceKind.RawFile)
            {
                data[index] = new BoundedInputStream(sources[index]);
                continue;
            }
            UnityFsBundle bundle = OpenSource(sources[index]);
            opened.Add(bundle);
            ValidateSource(bundle, descriptor);
            data[index] = bundle.Data;
            directories[index] = bundle.Directory;
        }
        return (data, directories);
    }

    /// <summary>
    /// The verified source streams and lazily built chunk index for one release baseline.
    /// </summary>
    /// <remarks>
    /// The publisher must hold its read-only native handles until generation and final verification
    /// are complete. Use serially and dispose before closing the native handles.
    /// </remarks>
    internal sealed class ReleaseSourceIndex : IDisposable
    {
        private readonly IReadOnlyList<BundleRecipeSource> _descriptors;
        private readonly IReadOnlyList<Stream> _sources;
        private readonly long[] _positions;
        private readonly BundleRecipeGenerationOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly List<UnityFsBundle> _opened = new();
        private Dictionary<string, (int SourceIndex, long Offset, int Length)>? _chunks;

        /// <summary>
        /// Verify and bind the native source streams for a release publication.
        /// </summary>
        /// <param name="descriptors">The validated stock descriptors in source-index order.</param>
        /// <param name="sources">The stable native read-only handles in descriptor order.</param>
        /// <param name="options">The matching and generation budgets.</param>
        /// <param name="cancellationToken">The publication cancellation token.</param>
        internal ReleaseSourceIndex(IReadOnlyList<BundleRecipeSource> descriptors, IReadOnlyList<Stream> sources,
            BundleRecipeGenerationOptions options, CancellationToken cancellationToken)
        {
            _descriptors = descriptors;
            _sources = sources;
            _positions = sources.Select(source => source.Position).ToArray();
            _options = options;
            _cancellationToken = cancellationToken;
            try
            {
                (Data, Directories) = OpenGenerationSources(descriptors, sources, _opened);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal Stream[] Data { get; }
        internal List<AssetBundleDirectoryInfo>?[] Directories { get; }
        internal CancellationToken CancellationToken => _cancellationToken;

        internal Dictionary<string, (int SourceIndex, long Offset, int Length)> GetChunks()
        {
            if (_chunks is not null)
                return _chunks;
            using var state = new GenerationState(Data, Stream.Null, _options);
            state.IndexSources(Directories, _cancellationToken);
            return _chunks = state.SourceChunks;
        }

        /// <summary>
        /// Generate one target recipe against the bound release baseline.
        /// </summary>
        /// <param name="target">The native target stream.</param>
        /// <param name="literalPack">The empty literal pack stream.</param>
        /// <param name="canonicalTarget">The optional empty canonical target stream.</param>
        /// <param name="hints">The optional verified reuse claims.</param>
        /// <param name="expectedNativeTarget">The native length and digest from the frozen target contract.</param>
        /// <returns>The generated recipe and byte measurements.</returns>
        internal BundleRecipeGenerationResult Generate(Stream target, Stream literalPack, Stream? canonicalTarget,
            IReadOnlyList<BundleRecipeRangeHint>? hints, (long Length, string Sha256) expectedNativeTarget)
        {
            if (canonicalTarget is not null)
                RequireGenerationOutput(canonicalTarget, nameof(canonicalTarget));
            return GenerateCore(_descriptors, _sources, target, literalPack, canonicalTarget, _options, hints, this, expectedNativeTarget);
        }

        /// <summary>
        /// Verify the bound native streams again before the publisher seals the result.
        /// </summary>
        internal void Verify()
        {
            for (int index = 0; index < _sources.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                VerifyNativeIdentity(_sources[index], _descriptors[index]);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var bundle in _opened)
            {
                bundle.Dispose();
            }
            for (int index = 0; index < _sources.Count; index++)
            {
                _sources[index].Position = _positions[index];
            }
        }
    }

    private static void RequireGenerationOutput(Stream output, string name)
    {
        RequireInput(output, name);
        if (!output.CanWrite || output.Length != 0 || output.Position != 0)
            throw new ArgumentException("Generation outputs must be writable, empty and positioned at zero.", name);
    }

    private static void VerifyNativeIdentity(Stream source, BundleRecipeSource descriptor)
    {
        if (source.Length != descriptor.Length ||
            !string.Equals(HashFile(source, descriptor.Length), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A native source file does not match its expected identity.");
    }

    private static BundleRecipeHeader DescribeHeader(AssetBundleHeader header) => new()
    {
        FormatVersion = header.Version,
        GenerationVersion = header.GenerationVersion,
        EngineVersion = header.EngineVersion,
        Flags = (uint)header.FileStreamHeader.Flags,
    };

    private static BundleRecipeSource CloneSource(BundleRecipeSource source) => new()
    {
        Kind = source.Kind,
        Sha256 = source.Sha256,
        Length = source.Length,
        DataLength = source.DataLength,
        Header = source.Header is null ? null : new BundleRecipeHeader
        {
            FormatVersion = source.Header.FormatVersion,
            GenerationVersion = source.Header.GenerationVersion,
            EngineVersion = source.Header.EngineVersion,
            Flags = source.Header.Flags,
        },
    };

    private static void ValidateGenerationDirectory(IReadOnlyList<AssetBundleDirectoryInfo> directory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in directory)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.Name.IndexOf('\0') >= 0 || !names.Add(entry.Name))
                throw new InvalidDataException("The target directory has an invalid or duplicate name.");
            if (entry.DecompressedSize < 0 || entry.DecompressedSize > ((long)int.MaxValue * MAX_READ_SIZE) - total)
                throw new InvalidDataException("The target data exceeds the canonical writer's block count range.");
            total += entry.DecompressedSize;
        }
    }

    private static List<BundleRecipeRangeHint> ValidateRangeHints(IReadOnlyList<BundleRecipeRangeHint>? supplied,
        IReadOnlyList<AssetBundleDirectoryInfo> targetDirectory, IReadOnlyList<BundleRecipeSource> sources, int budget)
    {
        int count = supplied?.Count ?? 0;
        if (count > budget)
            throw new InvalidOperationException($"Range hint metadata budget of {budget} records is exhausted.");
        var hints = new List<BundleRecipeRangeHint>(count);
        if (supplied is null)
            return hints;
        foreach (var hint in supplied)
        {
            if (hint is null)
                throw new InvalidDataException("A range hint is null.");
            if (hint.TargetEntryIndex < 0 || hint.TargetEntryIndex >= targetDirectory.Count)
                throw new InvalidDataException($"Range hint target entry index {hint.TargetEntryIndex} is invalid.");
            if (hint.SourceIndex < 0 || hint.SourceIndex >= sources.Count)
                throw new InvalidDataException($"Range hint source index {hint.SourceIndex} is invalid.");
            if (hint.Length <= 0)
                throw new InvalidDataException("Range hint lengths must be positive.");
            ValidateHash(hint.Sha256);
            ValidateRange(hint.TargetOffset, hint.Length, targetDirectory[hint.TargetEntryIndex].DecompressedSize);
            ValidateRange(hint.SourceOffset, hint.Length, sources[hint.SourceIndex].DataLength);
            hints.Add(new BundleRecipeRangeHint
            {
                TargetEntryIndex = hint.TargetEntryIndex,
                TargetOffset = hint.TargetOffset,
                SourceIndex = hint.SourceIndex,
                SourceOffset = hint.SourceOffset,
                Length = hint.Length,
                Sha256 = hint.Sha256,
            });
        }
        hints.Sort((left, right) => left.TargetEntryIndex != right.TargetEntryIndex
            ? left.TargetEntryIndex.CompareTo(right.TargetEntryIndex)
            : left.TargetOffset.CompareTo(right.TargetOffset));
        for (int index = 1; index < hints.Count; index++)
        {
            var previous = hints[index - 1];
            var current = hints[index];
            if (previous.TargetEntryIndex == current.TargetEntryIndex && current.TargetOffset - previous.TargetOffset < previous.Length)
                throw new InvalidDataException("Range hints overlap in a target entry.");
        }
        return hints;
    }

    private static bool HasUnhintedContent(IReadOnlyList<AssetBundleDirectoryInfo> directory, IReadOnlyList<BundleRecipeRangeHint> hints)
    {
        int hintIndex = 0;
        for (int entryIndex = 0; entryIndex < directory.Count; entryIndex++)
        {
            long cursor = 0;
            while (hintIndex < hints.Count && hints[hintIndex].TargetEntryIndex == entryIndex)
            {
                var hint = hints[hintIndex++];
                if (hint.TargetOffset != cursor)
                    return true;
                cursor += hint.Length;
            }
            if (cursor != directory[entryIndex].DecompressedSize)
                return true;
        }
        return false;
    }

    private static ulong[] CreateGearTable()
    {
        var table = new ulong[256];
        ulong state = 0x4F4D4E4950415443;
        for (int index = 0; index < table.Length; index++)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15;
                ulong mixed = state;
                mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9;
                mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EB;
                table[index] = mixed ^ (mixed >> 31);
            }
        }
        return table;
    }

    // Indexes retain only hash/range records. Chunk bytes live in reusable bounded buffers.
    private class GenerationState : IDisposable
    {
        private readonly IReadOnlyList<Stream> _sources;
        private readonly Stream _pack;
        private readonly int _chunkBudget;
        private readonly int _segmentBudget;
        internal Dictionary<string, (int SourceIndex, long Offset, int Length)> SourceChunks { get; set; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Offset, int Length)> _literalChunks = new(StringComparer.Ordinal);
        private readonly byte[] _chunk = new byte[CDC_MAX_CHUNK_SIZE];
        private readonly byte[] _read = new byte[CDC_READ_SIZE];
        private readonly byte[] _verify = new byte[CDC_READ_SIZE];
        private readonly SHA256 _sha256 = SHA256.Create();

        /// <summary>
        /// Creates per-generation indexes and reusable chunk buffers.
        /// </summary>
        /// <param name="sources">Source coordinate streams in index order.</param>
        /// <param name="pack">The empty literal pack output.</param>
        /// <param name="options">The chunk-record and segment budgets.</param>
        public GenerationState(IReadOnlyList<Stream> sources, Stream pack, BundleRecipeGenerationOptions options)
        {
            _sources = sources;
            _pack = pack;
            _chunkBudget = options.MaxIndexedChunks;
            _segmentBudget = options.MaxRecipeSegments;
        }

        /// <summary>
        /// Gets the measured generation counters.
        /// </summary>
        public BundleRecipeGenerationMetrics Metrics { get; } = new();

        /// <summary>
        /// Gets unique literal chunks in pack offset order.
        /// </summary>
        public List<BundleRecipeLiteralChunk> Literals { get; } = new();

        public void IndexSources(IReadOnlyList<List<AssetBundleDirectoryInfo>?> directories,
            CancellationToken cancellationToken)
        {
            for (int index = 0; index < _sources.Count; index++)
            {
                int sourceIndex = index;
                Stream source = _sources[index];
                void Index(long offset, byte[] bytes, int length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IndexSource(sourceIndex, offset, bytes, length);
                }
                var directory = directories[index];
                if (directory is null)
                {
                    WalkChunks(source, 0, source.Length, Index);
                    continue;
                }
                foreach (var entry in directory)
                {
                    WalkChunks(source, entry.Offset, entry.DecompressedSize, Index);
                }
            }
        }

        /// <summary>
        /// Streams deterministic chunks from one validated entry or raw-file range.
        /// </summary>
        /// <param name="input">The readable coordinate stream.</param>
        /// <param name="offset">The range start.</param>
        /// <param name="length">The range length.</param>
        /// <param name="consume">The callback receiving reusable bytes and their coordinate range.</param>
        public void WalkChunks(Stream input, long offset, long length, Action<long, byte[], int> consume)
        {
            ValidateRange(offset, length, input.Length);
            long nextRead = offset;
            long remaining = length;
            long chunkOffset = offset;
            int filled = 0;
            ulong gear = 0;
            while (remaining > 0)
            {
                // Callbacks can seek a source for exact verification, including this same stream.
                input.Position = nextRead;
                int read = input.Read(_read, 0, (int)Math.Min(_read.Length, remaining));
                if (read == 0)
                    throw new EndOfStreamException("A CDC input range ended early.");
                nextRead += read;
                remaining -= read;
                for (int index = 0; index < read; index++)
                {
                    byte value = _read[index];
                    _chunk[filled++] = value;
                    gear = unchecked((gear << 1) + GEAR_TABLE[value]);
                    if (filled < CDC_MAX_CHUNK_SIZE && (filled < CDC_MIN_CHUNK_SIZE || (gear & CDC_BOUNDARY_MASK) != 0))
                        continue;
                    consume(chunkOffset, _chunk, filled);
                    chunkOffset += filled;
                    filled = 0;
                    gear = 0;
                }
            }
            if (filled > 0)
            {
                consume(chunkOffset, _chunk, filled);
            }
        }

        /// <summary>
        /// Indexes one source chunk with deterministic duplicate selection and exact comparison.
        /// </summary>
        /// <param name="sourceIndex">The native source descriptor index.</param>
        /// <param name="offset">The chunk coordinate offset.</param>
        /// <param name="bytes">The reusable chunk bytes.</param>
        /// <param name="length">The chunk length.</param>
        public void IndexSource(int sourceIndex, long offset, byte[] bytes, int length)
        {
            Metrics.SourceBytesIndexed = checked(Metrics.SourceBytesIndexed + length);
            Metrics.SourceChunksScanned = checked(Metrics.SourceChunksScanned + 1);
            string hash = Hex.Encode(_sha256.ComputeHash(bytes, 0, length));
            if (SourceChunks.TryGetValue(hash, out var previous))
            {
                CompareRange(_sources[previous.SourceIndex], previous.Offset, previous.Length, bytes, length);
                if (sourceIndex < previous.SourceIndex || (sourceIndex == previous.SourceIndex && offset < previous.Offset))
                {
                    SourceChunks[hash] = (sourceIndex, offset, length);
                }
                return;
            }
            RequireChunkBudget();
            SourceChunks.Add(hash, (sourceIndex, offset, length));
            Metrics.UniqueSourceChunks++;
        }

        /// <summary>
        /// Emits a verified source range or a deduplicated literal range for one target chunk.
        /// </summary>
        /// <param name="entry">The target recipe entry.</param>
        /// <param name="bytes">The reusable target chunk bytes.</param>
        /// <param name="length">The target chunk length.</param>
        public void MatchTarget(BundleRecipeEntry entry, byte[] bytes, int length)
        {
            Metrics.TargetDataBytes += length;
            Metrics.TargetChunksScanned++;
            string hash = Hex.Encode(_sha256.ComputeHash(bytes, 0, length));
            if (SourceChunks.TryGetValue(hash, out var source))
            {
                CompareRange(_sources[source.SourceIndex], source.Offset, source.Length, bytes, length);
                AppendSegment(entry, BundleRecipeSegmentKind.Source, source.SourceIndex, source.Offset, length);
                Metrics.ReusedBytes += length;
                return;
            }

            Metrics.LiteralBytes += length;
            if (_literalChunks.TryGetValue(hash, out var literal))
            {
                CompareRange(_pack, literal.Offset, literal.Length, bytes, length);
                AppendSegment(entry, BundleRecipeSegmentKind.Payload, -1, literal.Offset, length);
                Metrics.DeduplicatedLiteralBytes += length;
                return;
            }
            RequireChunkBudget();
            long offset = _pack.Length;
            AppendSegment(entry, BundleRecipeSegmentKind.Payload, -1, offset, length);
            _pack.Position = offset;
            _pack.Write(bytes, 0, length);
            CompareRange(_pack, offset, length, bytes, length);
            _literalChunks.Add(hash, (offset, length));
            Literals.Add(new BundleRecipeLiteralChunk { Sha256 = hash, Offset = offset, Length = length });
            Metrics.UniqueLiteralChunks++;
        }

        /// <summary>
        /// Verifies both SHA-256 range identities and their exact bytes through reusable buffers.
        /// </summary>
        /// <param name="target">The target's decompressed data stream.</param>
        /// <param name="targetOffset">The hinted offset in the whole target data stream.</param>
        /// <param name="hint">The validated and copied range hint.</param>
        public void VerifyHint(Stream target, long targetOffset, BundleRecipeRangeHint hint)
        {
            Stream source = _sources[hint.SourceIndex];
            ValidateRange(targetOffset, hint.Length, target.Length);
            ValidateRange(hint.SourceOffset, hint.Length, source.Length);
            using var targetHash = SHA256.Create();
            using var sourceHash = SHA256.Create();
            long verified = 0;
            while (verified < hint.Length)
            {
                int count = (int)Math.Min(_read.Length, hint.Length - verified);
                ReadHintBytes(target, targetOffset + verified, _read, count);
                ReadHintBytes(source, hint.SourceOffset + verified, _verify, count);
                targetHash.TransformBlock(_read, 0, count, _read, 0);
                sourceHash.TransformBlock(_verify, 0, count, _verify, 0);
                if (!_read.AsSpan(0, count).SequenceEqual(_verify.AsSpan(0, count)))
                    throw new InvalidDataException("Range hint target and source bytes differ.");
                verified += count;
            }
            targetHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sourceHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (!string.Equals(Hex.Encode(targetHash.Hash!), hint.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Range hint target SHA-256 does not match its expected identity.");
            if (!string.Equals(Hex.Encode(sourceHash.Hash!), hint.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Range hint source SHA-256 does not match its expected identity.");
        }

        /// <summary>
        /// Emits a previously verified source range and records semantic payload reuse separately.
        /// </summary>
        /// <param name="entry">The target recipe entry.</param>
        /// <param name="hint">The verified target/source mapping.</param>
        public void AcceptHint(BundleRecipeEntry entry, BundleRecipeRangeHint hint)
        {
            AppendSegment(entry, BundleRecipeSegmentKind.Source, hint.SourceIndex, hint.SourceOffset, hint.Length);
            Metrics.TargetDataBytes = checked(Metrics.TargetDataBytes + hint.Length);
            Metrics.ReusedBytes = checked(Metrics.ReusedBytes + hint.Length);
            Metrics.HintedReusedBytes = checked(Metrics.HintedReusedBytes + hint.Length);
            Metrics.VerifiedRangeHints++;
        }

        /// <inheritdoc />
        public void Dispose() => _sha256.Dispose();

        private void RequireChunkBudget()
        {
            if (SourceChunks.Count + _literalChunks.Count >= _chunkBudget)
                throw new InvalidOperationException($"CDC chunk index budget of {_chunkBudget} records is exhausted.");
        }

        private void CompareRange(Stream input, long offset, int storedLength, byte[] bytes, int length)
        {
            if (storedLength != length)
                throw new InvalidDataException("SHA-256 chunk identities collide with different lengths.");
            ValidateRange(offset, length, input.Length);
            int compared = 0;
            while (compared < length)
            {
                input.Position = offset + compared;
                int read = input.Read(_verify, 0, Math.Min(_verify.Length, length - compared));
                if (read == 0)
                    throw new EndOfStreamException("A chunk verification range ended early.");
                if (!_verify.AsSpan(0, read).SequenceEqual(bytes.AsSpan(compared, read)))
                    throw new InvalidDataException("A source or literal chunk changed, or a SHA-256 collision was encountered.");
                compared += read;
            }
        }

        private static void ReadHintBytes(Stream input, long offset, byte[] buffer, int count)
        {
            int filled = 0;
            while (filled < count)
            {
                input.Position = offset + filled;
                int read = input.Read(buffer, filled, count - filled);
                if (read == 0)
                    throw new EndOfStreamException("A range hint verification stream ended early.");
                filled += read;
            }
        }

        private void AppendSegment(BundleRecipeEntry entry, BundleRecipeSegmentKind kind, int sourceIndex, long offset, long length)
        {
            if (entry.Segments.Count > 0)
            {
                var previous = entry.Segments[entry.Segments.Count - 1];
                if (previous.Kind == kind && previous.SourceIndex == sourceIndex && previous.Offset + previous.Length == offset)
                {
                    previous.Length = checked(previous.Length + length);
                    return;
                }
            }
            if (Metrics.OutputSegments >= _segmentBudget)
                throw new InvalidOperationException($"Recipe segment budget of {_segmentBudget} records is exhausted.");
            entry.Segments.Add(new BundleRecipeSegment { Kind = kind, SourceIndex = sourceIndex, Offset = offset, Length = length });
            Metrics.OutputSegments++;
        }
    }
}

/// <summary>
/// Explicit bounds for in-memory chunk records and emitted recipe segments.
/// </summary>
public class BundleRecipeGenerationOptions
{
    /// <summary>
    /// Gets or sets the combined unique source/literal chunk-record limit, defaulting to 262144.
    /// </summary>
    /// <remarks>
    /// Each record retains a SHA-256 hex key and a coordinate tuple, never chunk payload bytes.
    /// Dictionary capacity and managed-object overhead are implementation costs, not a byte budget.
    /// </remarks>
    public int MaxIndexedChunks { get; set; } = 262144;

    /// <summary>
    /// Gets or sets the merged recipe segment limit, defaulting to 1000000.
    /// </summary>
    public int MaxRecipeSegments { get; set; } = 1000000;

    /// <summary>
    /// Gets or sets the supplied range-hint metadata record limit, defaulting to 65536.
    /// </summary>
    /// <remarks>
    /// Hints are copied and sorted once. This count bounds retained hint records, not reader caches,
    /// metadata, managed-object overhead or a total byte allocation budget.
    /// </remarks>
    public int MaxRangeHints { get; set; } = 65536;
}

/// <summary>
/// A persisted optimization claim mapping a target entry range to an expected native source range.
/// </summary>
/// <remarks>
/// A locator or recovery ledger supplies these coordinates. The generator verifies both range hashes
/// and exact bytes. Names do not identify either side, and a hint never authorizes changing target data.
/// </remarks>
public class BundleRecipeRangeHint
{
    /// <summary>
    /// Gets or sets the target directory entry index, or zero for a raw target. An omitted index is invalid.
    /// </summary>
    public int TargetEntryIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the nonnegative entry-relative target byte offset.
    /// </summary>
    public long TargetOffset { get; set; }

    /// <summary>
    /// Gets or sets the source descriptor index. An omitted index is invalid.
    /// </summary>
    public int SourceIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the nonnegative offset in the source descriptor's declared coordinate space.
    /// </summary>
    public long SourceOffset { get; set; }

    /// <summary>
    /// Gets or sets the positive range length in bytes.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets the expected SHA-256 of both exact ranges as 64 hexadecimal characters.
    /// </summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// A generated recipe and the literal catalog needed for later cross-recipe pack aggregation.
/// </summary>
public class BundleRecipeGenerationResult
{
    internal BundleRecipeGenerationResult(BundleRecipe recipe, string nativeTargetSha256, long nativeTargetLength,
        BundleRecipeGenerationMetrics metrics, IReadOnlyList<BundleRecipeLiteralChunk> literalChunks)
    {
        Recipe = recipe;
        NativeTargetSha256 = nativeTargetSha256;
        NativeTargetLength = nativeTargetLength;
        Metrics = metrics;
        LiteralChunks = literalChunks;
    }

    /// <summary>
    /// Gets the recipe for the exact bytes selected by the target profile.
    /// </summary>
    public BundleRecipe Recipe { get; }

    /// <summary>
    /// Gets the SHA-256 of the original target file, unchanged for a raw target.
    /// </summary>
    public string NativeTargetSha256 { get; }

    /// <summary>
    /// Gets the original target file length in bytes.
    /// </summary>
    public long NativeTargetLength { get; }

    /// <summary>
    /// Gets counters and elapsed time for this generation.
    /// </summary>
    public BundleRecipeGenerationMetrics Metrics { get; }

    /// <summary>
    /// Gets unique literal SHA-256 identities and pack ranges in append order.
    /// </summary>
    public IReadOnlyList<BundleRecipeLiteralChunk> LiteralChunks { get; }
}

/// <summary>
/// A persisted literal chunk identity and range, without a filesystem or hosting location.
/// </summary>
public class BundleRecipeLiteralChunk
{
    /// <summary>
    /// Gets or sets the SHA-256 of the chunk's exact bytes.
    /// </summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// Gets or sets the offset in the generation's single literal pack.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Gets or sets the chunk length in bytes.
    /// </summary>
    public int Length { get; set; }
}

/// <summary>
/// Measured logical byte counts, index sizes and elapsed time for CDC generation.
/// </summary>
public class BundleRecipeGenerationMetrics
{
    /// <summary>
    /// Gets the source entry or raw-file bytes scanned, counting overlapping entries independently.
    /// </summary>
    public long SourceBytesIndexed { get; internal set; }

    /// <summary>
    /// Gets the source chunks scanned, including duplicate contents.
    /// </summary>
    public long SourceChunksScanned { get; internal set; }

    /// <summary>
    /// Gets the unique source chunk records retained by the index.
    /// </summary>
    public int UniqueSourceChunks { get; internal set; }

    /// <summary>
    /// Gets the canonical bundle entry or complete raw target bytes matched.
    /// </summary>
    public long TargetDataBytes { get; internal set; }

    /// <summary>
    /// Gets CDC target gap chunks scanned before merging adjacent ranges, excluding verified hints.
    /// </summary>
    public long TargetChunksScanned { get; internal set; }

    /// <summary>
    /// Gets the target bytes supplied by source ranges.
    /// </summary>
    public long ReusedBytes { get; internal set; }

    /// <summary>
    /// Gets target bytes reused through checked range hints.
    /// </summary>
    public long HintedReusedBytes { get; internal set; }

    /// <summary>
    /// Gets target bytes reused through ordinary CDC matching, excluding hinted reuse.
    /// </summary>
    public long CdcReusedBytes => ReusedBytes - HintedReusedBytes;

    /// <summary>
    /// Gets the number of verified range hints emitted into the recipe before segment merging.
    /// </summary>
    public int VerifiedRangeHints { get; internal set; }

    /// <summary>
    /// Gets target bytes supplied by literal ranges, including repeated references to the same bytes.
    /// </summary>
    public long LiteralBytes { get; internal set; }

    /// <summary>
    /// Gets logical literal bytes reused from already stored chunks.
    /// </summary>
    public long DeduplicatedLiteralBytes { get; internal set; }

    /// <summary>
    /// Gets the unique literal chunk records retained by the index and catalog.
    /// </summary>
    public int UniqueLiteralChunks { get; internal set; }

    /// <summary>
    /// Gets the final literal pack size in bytes.
    /// </summary>
    public long LiteralPackBytes { get; internal set; }

    /// <summary>
    /// Gets the combined unique source and literal chunk-record count governed by MaxIndexedChunks.
    /// </summary>
    public int IndexRecords => UniqueSourceChunks + UniqueLiteralChunks;

    /// <summary>
    /// Gets the merged recipe segment count governed by MaxRecipeSegments.
    /// </summary>
    public int OutputSegments { get; internal set; }

    /// <summary>
    /// Gets elapsed generation time, including identity hashes and canonicalization, in milliseconds.
    /// </summary>
    public double ElapsedMilliseconds { get; internal set; }
}
