using AssetsTools.NET;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

/// <summary>
/// A persisted recipe for reconstructing a canonical UnityFS bundle or exact raw file from ranges.
/// </summary>
/// <remarks>
/// Source offsets address the coordinate space declared by the indexed source descriptor. Payload
/// offsets address one external literal pack stream. Shared content-addressed pack and chunk resolution
/// belongs to the generator layer. Entry names are metadata and are never used as filesystem paths.
    /// The recipe and its input streams must remain unchanged during reconstruction.
    /// Version 1 selects canonical UnityFS output. Version 2 selects exact raw-file output with the
    /// raw-file profile, no target header and one unnamed content entry with zero flags.
/// </remarks>
public partial class BundleRecipe
{
    /// <summary>
    /// The schema version emitted for canonical UnityFS targets.
    /// </summary>
    public const int CURRENT_VERSION = 1;

    /// <summary>
    /// The schema version for exact raw-file targets with one unnamed content entry and no header.
    /// </summary>
    public const int RAW_TARGET_VERSION = 2;

    /// <summary>
    /// The raw-file profile that concatenates ranges without wrapping or transforming bytes.
    /// </summary>
    public const string RAW_PACKING_PROFILE = "raw-file-exact-v1";

    /// <summary>
    /// The canonical packing profile implemented by the shared bundle writer.
    /// </summary>
    /// <remarks>
    /// AssetsTools.NET 3.0.5 and its bundled LZ4ps Encode32HC implementation pack 128 KiB data blocks.
    /// Blocks that do not become smaller are stored raw. The directory and block list are uncompressed
    /// at the front, followed by 16-byte alignment. The block-list hash is zero. Header format and
    /// Unity version strings come from TargetHeader. Any change to these bytes requires a new profile.
    /// </remarks>
    public const string CANONICAL_PACKING_PROFILE = "unityfs-128k-lz4hc-assetstools-3.0.5-v1";

    /// <summary>
    /// The header flags emitted by the canonical packing profile.
    /// </summary>
    public const uint CANONICAL_HEADER_FLAGS =
        (uint)(AssetBundleFSHeaderFlags.HasDirectoryInfo | AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart);

    private const int MAX_READ_SIZE = 128 * 1024;

    /// <summary>
    /// Gets or sets the recipe schema version. An omitted version is invalid.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets the exact packing profile identifier.
    /// </summary>
    public string PackingProfile { get; set; } = "";

    /// <summary>
    /// Gets or sets the generator's matching profile as provenance, without affecting reconstruction.
    /// </summary>
    public string MatchingProfile { get; set; } = "";

    /// <summary>
    /// Gets or sets the ordered native source identities and their range coordinate spaces.
    /// </summary>
    public List<BundleRecipeSource> Sources { get; set; } = new();

    /// <summary>
    /// Gets or sets the length of the single external literal pack stream in bytes.
    /// </summary>
    public long PayloadLength { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 of the exact reconstructed target file as 64 hexadecimal characters.
    /// </summary>
    public string TargetSha256 { get; set; } = "";

    /// <summary>
    /// Gets or sets the exact reconstructed target file length in bytes, including zero for raw files.
    /// </summary>
    public long TargetLength { get; set; }

    /// <summary>
    /// Gets or sets the UnityFS target header metadata, or null for a raw target.
    /// </summary>
    public BundleRecipeHeader? TargetHeader { get; set; }

    /// <summary>
    /// Gets or sets the UnityFS directory entries or the raw profile's single unnamed content entry.
    /// </summary>
    public List<BundleRecipeEntry> Entries { get; set; } = new();

    /// <summary>
    /// Reconstructs and verifies the exact target file through the declared output profile.
    /// </summary>
    /// <remarks>
    /// All streams remain open on success and failure. Referenced input positions are restored.
    /// Every source descriptor is validated, but only descriptors referenced by source segments are
    /// opened and hashed. Unused stream slots may be null and are never accessed. Even a zero-length
    /// source segment binds its descriptor. Referenced source stream objects must differ from the
    /// payload and output streams. Output storage must be separate from all referenced
    /// inputs and the payload, and output must be empty at position zero. On success output is
    /// positioned at its end. On failure output may contain partial or unverified bytes that the
    /// caller must discard. This operation provides verification, not an installation transaction.
    /// Read requests are capped at 128 KiB, which is not a total memory budget. Each referenced UnityFS
    /// reader holds directory and block metadata and caches up to eight decompressed storage blocks
    /// of the source's own block sizes. This primitive adds no source block-size budget. Large source
    /// blocks can therefore allocate large buffers. Whole source files are not copied into arrays.
    /// </remarks>
    /// <param name="sources">
    /// Native file streams in descriptor order, with exactly one slot per descriptor. Referenced
    /// slots must be readable and seekable. An empty list supports targets with no source segments.
    /// </param>
    /// <param name="payload">A readable, seekable stream over the external literal bytes.</param>
    /// <param name="output">An empty readable, writable, seekable stream for the target file.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when the source list, a referenced source stream, the payload or output is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when stream capabilities, identity or initial output state are unsuitable.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown when the recipe, source bundle or reconstructed target does not match its contract.
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown when a source or payload read ends before the declared range ends.
    /// </exception>
    public void Reconstruct(IReadOnlyList<Stream?> sources, Stream payload, Stream output)
    {
        if (sources is null)
            throw new ArgumentNullException(nameof(sources));
        RequireInput(payload, nameof(payload));
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (!output.CanRead || !output.CanWrite || !output.CanSeek)
            throw new ArgumentException("Output must be readable, writable and seekable.", nameof(output));
        if (ReferenceEquals(payload, output))
            throw new ArgumentException("Payload and output must be distinct streams.");
        if (output.Length != 0 || output.Position != 0)
            throw new ArgumentException("Output must be empty and positioned at zero.", nameof(output));

        bool[] referenced = Validate(sources.Count, payload.Length);
        var sourcePositions = new long[sources.Count];
        for (int index = 0; index < sources.Count; index++)
        {
            if (!referenced[index])
                continue;
            Stream source = sources[index]!;
            RequireInput(source, $"sources[{index}]");
            if (ReferenceEquals(source, payload) || ReferenceEquals(source, output))
                throw new ArgumentException("Referenced sources must be distinct from payload and output.");
            if (source.Length != Sources[index].Length)
                throw new InvalidDataException($"Source {index} file length does not match its descriptor.");
            sourcePositions[index] = source.Position;
        }

        long payloadPosition = payload.Position;
        var sourceData = new Stream?[sources.Count];
        var bundles = new List<UnityFsBundle>();
        try
        {
            for (int index = 0; index < sources.Count; index++)
            {
                if (!referenced[index])
                    continue;
                Stream source = sources[index]!;
                var descriptor = Sources[index];
                if (!string.Equals(HashFile(source, descriptor.Length), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Source {index} file SHA-256 does not match its descriptor.");

                if (descriptor.Kind == BundleRecipeSourceKind.RawFile)
                {
                    sourceData[index] = new BoundedInputStream(source);
                    continue;
                }

                // UnityFsBundle owns a view whose disposal leaves the caller's native file open.
                UnityFsBundle bundle = OpenSource(source);
                bundles.Add(bundle);
                ValidateSource(bundle, descriptor);
                sourceData[index] = bundle.Data;
            }

            if (Version == RAW_TARGET_VERSION)
            {
                using var content = new RecipeEntryStream(Entries[0], sourceData, payload);
                content.CopyTo(output, MAX_READ_SIZE);
            }
            else
            {
                WriteCanonicalTarget(sourceData, payload, output);
            }

            if (output.Length != TargetLength)
                throw new InvalidDataException("The reconstructed target length does not match the recipe.");
            if (!string.Equals(HashFile(output, TargetLength), TargetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The reconstructed target SHA-256 does not match the recipe.");
            output.Position = TargetLength;
        }
        finally
        {
            foreach (var bundle in bundles)
            {
                bundle.Dispose();
            }
            for (int index = 0; index < sources.Count; index++)
            {
                if (!referenced[index])
                    continue;
                sources[index]!.Position = sourcePositions[index];
            }
            payload.Position = payloadPosition;
        }
    }

    private void WriteCanonicalTarget(IReadOnlyList<Stream?> sourceData, Stream payload, Stream output)
    {
        var entries = new List<KeyValuePair<AssetBundleDirectoryInfo, Stream>>(Entries.Count);
        foreach (var entry in Entries)
        {
            entries.Add(new KeyValuePair<AssetBundleDirectoryInfo, Stream>(
                new AssetBundleDirectoryInfo { Name = entry.Name, Flags = entry.Flags },
                new RecipeEntryStream(entry, sourceData, payload)));
        }

        var target = TargetHeader!;
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = target.FormatVersion,
            GenerationVersion = target.GenerationVersion,
            EngineVersion = target.EngineVersion,
            FileStreamHeader = new AssetBundleFSHeader
            {
                Flags = (AssetBundleFSHeaderFlags)target.Flags,
            },
        };
        BundleRewriter.WriteBundle(output, header, entries, compress: true);
    }

    private bool[] Validate(int sourceCount, long payloadLength)
    {
        if (Version is not (CURRENT_VERSION or RAW_TARGET_VERSION))
            throw new InvalidDataException($"Unsupported bundle recipe version {Version}.");
        bool isRaw = Version == RAW_TARGET_VERSION;
        if (PackingProfile != (isRaw ? RAW_PACKING_PROFILE : CANONICAL_PACKING_PROFILE))
            throw new InvalidDataException($"Unsupported bundle packing profile '{PackingProfile}'.");
        ValidateHash(TargetSha256);
        if (Sources is null || Sources.Count != sourceCount)
            throw new InvalidDataException("The source stream slots do not match the recipe's source descriptors.");
        foreach (var source in Sources)
        {
            if (source is null || source.Length < 0 || source.DataLength < 0)
                throw new InvalidDataException("A source descriptor is missing or has a negative length.");
            ValidateHash(source.Sha256);
            switch (source.Kind)
            {
                case BundleRecipeSourceKind.RawFile:
                    if (source.DataLength != source.Length || source.Header is not null)
                        throw new InvalidDataException("A raw source must use native file coordinates and have no bundle header.");
                    break;
                case BundleRecipeSourceKind.UnityFsData:
                    if (source.Length == 0)
                        throw new InvalidDataException("A UnityFS source file cannot be empty.");
                    ValidateHeader(source.Header);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported source coordinate space {source.Kind}.");
            }
        }
        if (PayloadLength < 0 || PayloadLength != payloadLength)
            throw new InvalidDataException("The literal payload length does not match the recipe.");
        if (Entries is null)
            throw new InvalidDataException("The target directory is missing.");
        if (isRaw)
        {
            if (TargetLength < 0 || TargetHeader is not null || Entries.Count != 1 ||
                Entries[0] is null || Entries[0].Name != "" || Entries[0].Flags != 0 || Entries[0].Length != TargetLength)
                throw new InvalidDataException("A raw target requires a nonnegative length, no header and exactly one unnamed content entry with zero flags.");
        }
        else
        {
            if (TargetLength <= 0)
                throw new InvalidDataException("The target bundle length must be positive.");
            ValidateHeader(TargetHeader);
            if (TargetHeader!.Flags != CANONICAL_HEADER_FLAGS)
                throw new InvalidDataException("Target header flags do not match the canonical packing profile.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var referenced = new bool[sourceCount];
        long total = 0;
        foreach (var entry in Entries)
        {
            if (entry is null || (!isRaw && (string.IsNullOrEmpty(entry.Name) || entry.Name.IndexOf('\0') >= 0)))
                throw new InvalidDataException("A target directory entry has an invalid name.");
            if (!names.Add(entry.Name))
                throw new InvalidDataException($"Duplicate target directory entry '{entry.Name}'.");
            if (entry.Length < 0 || (!isRaw && entry.Length > ((long)int.MaxValue * MAX_READ_SIZE) - total))
                throw new InvalidDataException("The target data length exceeds the canonical writer's block count range.");
            if (entry.Segments is null)
                throw new InvalidDataException($"Entry '{entry.Name}' has no segment list.");

            long length = 0;
            foreach (var segment in entry.Segments)
            {
                if (segment is null)
                    throw new InvalidDataException($"Entry '{entry.Name}' has a null segment.");
                long available;
                switch (segment.Kind)
                {
                    case BundleRecipeSegmentKind.Source:
                        if (segment.SourceIndex < 0 || segment.SourceIndex >= sourceCount)
                            throw new InvalidDataException($"Source index {segment.SourceIndex} is outside the descriptor list.");
                        referenced[segment.SourceIndex] = true;
                        available = Sources[segment.SourceIndex].DataLength;
                        break;
                    case BundleRecipeSegmentKind.Payload:
                        if (segment.SourceIndex != -1)
                            throw new InvalidDataException("A literal segment must not identify a source file.");
                        available = PayloadLength;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown segment kind {segment.Kind}.");
                }
                ValidateRange(segment.Offset, segment.Length, available);
                if (segment.Length > entry.Length - length)
                    throw new InvalidDataException($"Segments exceed the declared length of entry '{entry.Name}'.");
                length += segment.Length;
            }

            if (length != entry.Length)
                throw new InvalidDataException($"Segments do not fill entry '{entry.Name}'.");
            total += entry.Length;
        }

        return referenced;
    }

    private static void ValidateSource(UnityFsBundle bundle, BundleRecipeSource source)
    {
        var header = source.Header!;
        if (bundle.Header.Version != header.FormatVersion ||
            bundle.Header.GenerationVersion != header.GenerationVersion ||
            bundle.Header.EngineVersion != header.EngineVersion ||
            (uint)bundle.Header.FileStreamHeader.Flags != header.Flags)
            throw new InvalidDataException("The source header does not match the recipe.");
        if (bundle.Header.FileStreamHeader.TotalFileSize != source.Length || bundle.Data.Length != source.DataLength)
            throw new InvalidDataException("The source header or decompressed length does not match the recipe.");

        for (int index = 0; index < bundle.Blocks.Length; index++)
        {
            var block = bundle.Blocks[index];
            if (block.DecompressedSize == 0 || block.DecompressedSize > int.MaxValue || block.CompressedSize > int.MaxValue)
                throw new InvalidDataException("A source storage block has an unsupported size.");
            if ((block.Flags & 0x3F) == 0 && block.CompressedSize != block.DecompressedSize)
                throw new InvalidDataException("A raw source storage block has inconsistent sizes.");
            ValidateRange(bundle.Layout.FileStarts[index], block.CompressedSize, source.Length);
        }

        foreach (var entry in bundle.Directory)
        {
            ValidateRange(entry.Offset, entry.DecompressedSize, source.DataLength);
        }
    }

    private static void RequireInput(Stream stream, string name)
    {
        if (stream is null)
            throw new ArgumentNullException(name);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Input must be readable and seekable.", name);
    }

    private static UnityFsBundle OpenSource(Stream source)
    {
        try
        {
            return UnityFsBundle.Open(new BoundedInputStream(source));
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("The source bundle container format is unsupported.", exception);
        }
    }

    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64)
            throw new InvalidDataException("A SHA-256 must contain exactly 64 hexadecimal characters.");
        foreach (char character in hash)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                throw new InvalidDataException("A SHA-256 contains a non-hexadecimal character.");
        }
    }

    private static void ValidateHeader(BundleRecipeHeader? header)
    {
        if (header is null || header.FormatVersion is < 6 or > 8 ||
            header.GenerationVersion is null || header.EngineVersion is null ||
            header.GenerationVersion.IndexOf('\0') >= 0 || header.EngineVersion.IndexOf('\0') >= 0)
            throw new InvalidDataException("A UnityFS recipe header is invalid or unsupported.");
    }

    private static void ValidateRange(long offset, long length, long available)
    {
        // Subtraction rejects overflowing ranges without ever adding untrusted offsets and lengths.
        if (offset < 0 || length < 0 || offset > available || length > available - offset)
            throw new InvalidDataException($"Range ({offset}, {length}) is outside a stream of {available} bytes.");
    }

    private static string HashFile(Stream stream, long length, CancellationToken cancellationToken = default)
    {
        stream.Position = 0;
        using var view = new BoundedInputStream(stream, cancellationToken);
        string hash = Hex.Sha256(view);
        if (stream.Position != length)
            throw new EndOfStreamException("The stream ended before its declared file length.");
        return hash;
    }

    // This view caps requests made by hashing and the bundle reader and never owns the input.
    private class BoundedInputStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _cancellationToken;

        /// <summary>
        /// Creates a bounded non-owning input view.
        /// </summary>
        /// <param name="inner">The readable, seekable input.</param>
        /// <param name="cancellationToken">The cancellation token for reads.</param>
        public BoundedInputStream(Stream inner, CancellationToken cancellationToken = default)
        {
            _inner = inner;
            _cancellationToken = cancellationToken;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return _inner.Read(buffer, offset, Math.Min(count, MAX_READ_SIZE));
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The stream owns its segment cursor, while source data and literals remain caller-owned.
    private class RecipeEntryStream : Stream
    {
        private readonly BundleRecipeEntry _entry;
        private readonly IReadOnlyList<Stream?> _sources;
        private readonly Stream _payload;
        private int _segmentIndex;
        private long _withinSegment;
        private long _position;

        /// <summary>
        /// Creates a stream that concatenates validated recipe segments.
        /// </summary>
        /// <param name="entry">The validated target entry.</param>
        /// <param name="sources">The opened source coordinate streams in descriptor order.</param>
        /// <param name="payload">The literal payload.</param>
        public RecipeEntryStream(BundleRecipeEntry entry, IReadOnlyList<Stream?> sources, Stream payload)
        {
            _entry = entry;
            _sources = sources;
            _payload = payload;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _entry.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > Length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
                _segmentIndex = 0;
                _withinSegment = value;
                while (_segmentIndex < _entry.Segments.Count && _withinSegment >= _entry.Segments[_segmentIndex].Length)
                {
                    _withinSegment -= _entry.Segments[_segmentIndex++].Length;
                }
            }
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0 || _position == Length)
                return 0;

            while (_withinSegment == _entry.Segments[_segmentIndex].Length)
            {
                _segmentIndex++;
                _withinSegment = 0;
            }

            var segment = _entry.Segments[_segmentIndex];
            Stream input = segment.Kind == BundleRecipeSegmentKind.Source ? _sources[segment.SourceIndex]! : _payload;
            input.Position = segment.Offset + _withinSegment;
            int requested = (int)Math.Min(Math.Min(count, MAX_READ_SIZE), segment.Length - _withinSegment);
            int read = input.Read(buffer, offset, requested);
            if (read == 0)
                throw new EndOfStreamException("A recipe segment ended before its declared length.");
            _position += read;
            _withinSegment += read;
            return read;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            try
            {
                Position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(Position + offset),
                    SeekOrigin.End => checked(Length + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
            }
            catch (OverflowException exception)
            {
                throw new IOException("The recipe entry seek overflowed.", exception);
            }

            return Position;
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// A persisted native source file identity and the coordinate space available to copy segments.
/// </summary>
public class BundleRecipeSource
{
    /// <summary>
    /// Gets or sets whether ranges address native file bytes or decompressed UnityFS data.
    /// </summary>
    public BundleRecipeSourceKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 of the complete native file as 64 hexadecimal characters.
    /// </summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// Gets or sets the complete native file length in bytes, including UnityFS compression and headers.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets the available range length, equal to Length for raw files or decompressed size for bundles.
    /// </summary>
    public long DataLength { get; set; }

    /// <summary>
    /// Gets or sets the expected UnityFS source header, or null for native file coordinates.
    /// </summary>
    public BundleRecipeHeader? Header { get; set; }
}

/// <summary>
/// The coordinate space exposed by a native source descriptor.
/// </summary>
public enum BundleRecipeSourceKind
{
    /// <summary>
    /// Native file bytes, without parsing or decompression.
    /// </summary>
    RawFile = 1,

    /// <summary>
    /// The entire decompressed data stream of a UnityFS bundle.
    /// </summary>
    UnityFsData = 2,
}

/// <summary>
/// Persisted UnityFS format, Unity version strings and header flags.
/// </summary>
public class BundleRecipeHeader
{
    /// <summary>
    /// Gets or sets the UnityFS format version, currently supported from 6 through 8.
    /// </summary>
    public uint FormatVersion { get; set; }

    /// <summary>
    /// Gets or sets the bundle generation version string.
    /// </summary>
    public string GenerationVersion { get; set; } = "";

    /// <summary>
    /// Gets or sets the Unity engine version string.
    /// </summary>
    public string EngineVersion { get; set; } = "";

    /// <summary>
    /// Gets or sets the complete UnityFS header flags.
    /// </summary>
    public uint Flags { get; set; }
}

/// <summary>
/// A persisted target content entry whose contents concatenate source and literal ranges.
/// </summary>
public class BundleRecipeEntry
{
    /// <summary>
    /// Gets or sets the bundle directory name, or an empty string for the raw target's content entry.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the exact directory flags, or zero for a raw target.
    /// </summary>
    public uint Flags { get; set; }

    /// <summary>
    /// Gets or sets the decompressed bundle entry length or complete raw target length in bytes.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets the ranges in entry order. Their lengths must sum exactly to Length.
    /// </summary>
    public List<BundleRecipeSegment> Segments { get; set; } = new();
}

/// <summary>
/// A persisted range in an indexed source coordinate stream or the single external literal pack.
/// </summary>
public class BundleRecipeSegment
{
    /// <summary>
    /// Gets or sets the stream providing the range. An omitted or unknown kind is invalid.
    /// </summary>
    public BundleRecipeSegmentKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the source descriptor index for a copy range, or -1 for a literal range.
    /// </summary>
    public int SourceIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the nonnegative byte offset in the selected stream.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Gets or sets the nonnegative range length in bytes.
    /// </summary>
    public long Length { get; set; }
}

/// <summary>
/// The coordinate space supplying a recipe segment.
/// </summary>
public enum BundleRecipeSegmentKind
{
    /// <summary>
    /// The coordinate stream declared by the indexed source descriptor.
    /// </summary>
    Source = 1,

    /// <summary>
    /// The external literal payload stream.
    /// </summary>
    Payload = 2,
}
