using AssetsTools.NET;
using LZ4ps;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

/// <summary>
/// A UnityFS bundle opened for reading, with random access to its decompressed data and a map from
/// data offsets back to file offsets for the blocks that are stored uncompressed.
/// </summary>
// AssetsTools.NET's own AssetBundleFile hides where each block sits in the file and refuses LZ4
// bundles whose blocks differ in size, so the header and block list are parsed with its public
// readers and the data stream is our own.
internal sealed class UnityFsBundle : IDisposable
{
    private const ushort COMPRESSION_MASK = 0x3F;

    private readonly Stream _file;

    private UnityFsBundle(Stream file, AssetBundleHeader header, AssetBundleBlockAndDirInfo info)
    {
        _file = file;
        Header = header;
        Blocks = info.BlockInfos;
        Directory = info.DirectoryInfos;
        Layout = BlockLayout.Create(header.GetFileDataOffset(), info.BlockInfos);
        Data = new BlockDataStream(file, Layout);
    }

    /// <summary>
    /// Gets the bundle header, which carries the Unity version strings.
    /// </summary>
    public AssetBundleHeader Header { get; }

    /// <summary>
    /// Gets the storage blocks in data order.
    /// </summary>
    public AssetBundleBlockInfo[] Blocks { get; }

    /// <summary>
    /// Gets the files inside the bundle, with their offsets in the decompressed data stream.
    /// </summary>
    public List<AssetBundleDirectoryInfo> Directory { get; }

    /// <summary>
    /// Gets where every block sits in the data stream and in the file.
    /// </summary>
    public BlockLayout Layout { get; }

    /// <summary>
    /// Gets a seekable stream over the decompressed data, which the directory offsets index into.
    /// </summary>
    public Stream Data { get; }

    /// <summary>
    /// Reads a bundle's header and block list.
    /// </summary>
    /// <param name="file">A readable, seekable stream over the whole bundle. The bundle takes ownership of it.</param>
    /// <returns>The opened bundle.</returns>
    /// <exception cref="InvalidDataException">The stream is not a UnityFS bundle this reader supports.</exception>
    public static UnityFsBundle Open(Stream file)
    {
        try
        {
            var reader = new AssetsFileReader(file);
            reader.Position = 0;
            var header = new AssetBundleHeader();
            header.Read(reader);

            if (header.Signature != "UnityFS")
            {
                throw new InvalidDataException($"Unsupported bundle signature '{header.Signature}'.");
            }

            var flags = (uint)header.FileStreamHeader.Flags;
            var compressedSize = (int)header.FileStreamHeader.CompressedSize;
            var decompressedSize = (int)header.FileStreamHeader.DecompressedSize;

            file.Position = header.GetBundleInfoOffset();
            var raw = ReadExactly(file, compressedSize);
            var infoBytes = (flags & COMPRESSION_MASK) switch
            {
                0 => raw,
                2 or 3 => DecodeLz4(raw, decompressedSize),
                var other => throw new InvalidDataException($"Unsupported block info compression {other}."),
            };

            var info = new AssetBundleBlockAndDirInfo();
            info.Read(new AssetsFileReader(new MemoryStream(infoBytes)) { BigEndian = true });

            foreach (var block in info.BlockInfos)
            {
                var compression = block.Flags & COMPRESSION_MASK;
                if (compression is not (0 or 2 or 3))
                {
                    throw new InvalidDataException($"Unsupported block compression {compression}.");
                }
            }

            return new UnityFsBundle(file, header, info);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases the file stream.
    /// </summary>
    public void Dispose() => _file.Dispose();

    /// <summary>
    /// Decompresses one LZ4 block of a known size.
    /// </summary>
    /// <param name="compressed">The compressed block.</param>
    /// <param name="decompressedSize">The size the block decompresses to.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="InvalidDataException">The block is corrupt.</exception>
    internal static byte[] DecodeLz4(byte[] compressed, int decompressedSize)
    {
        try
        {
            return LZ4Codec.Decode32(compressed, 0, compressed.Length, decompressedSize);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"An LZ4 block of {compressed.Length} bytes did not decompress to {decompressedSize} bytes.", e);
        }
    }

    internal static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = stream.Read(buffer, read, count - read);
            if (chunk == 0)
            {
                throw new EndOfStreamException($"Expected {count} bytes, the stream ended after {read}.");
            }

            read += chunk;
        }

        return buffer;
    }
}

/// <summary>
/// Where each storage block of a bundle sits in the decompressed data stream and in the file.
/// </summary>
internal sealed class BlockLayout
{
    private BlockLayout(long[] dataStarts, long[] fileStarts, AssetBundleBlockInfo[] blocks, long dataLength)
    {
        DataStarts = dataStarts;
        FileStarts = fileStarts;
        Blocks = blocks;
        DataLength = dataLength;
    }

    /// <summary>
    /// Gets the data stream offset of every block.
    /// </summary>
    public long[] DataStarts { get; }

    /// <summary>
    /// Gets the file offset of every block.
    /// </summary>
    public long[] FileStarts { get; }

    /// <summary>
    /// Gets the blocks.
    /// </summary>
    public AssetBundleBlockInfo[] Blocks { get; }

    /// <summary>
    /// Gets the total decompressed data length.
    /// </summary>
    public long DataLength { get; }

    /// <summary>
    /// Gets a value indicating whether every block is stored uncompressed.
    /// </summary>
    public bool IsUncompressed => Blocks.All(block => (block.Flags & 0x3F) == 0);

    /// <summary>
    /// Builds the layout from the block list.
    /// </summary>
    /// <param name="fileDataOffset">The file offset of the first block.</param>
    /// <param name="blocks">The blocks in data order.</param>
    /// <returns>The layout.</returns>
    public static BlockLayout Create(long fileDataOffset, AssetBundleBlockInfo[] blocks)
    {
        var dataStarts = new long[blocks.Length];
        var fileStarts = new long[blocks.Length];
        long data = 0;
        var file = fileDataOffset;
        for (var index = 0; index < blocks.Length; index++)
        {
            dataStarts[index] = data;
            fileStarts[index] = file;
            data += blocks[index].DecompressedSize;
            file += blocks[index].CompressedSize;
        }

        return new BlockLayout(dataStarts, fileStarts, blocks, data);
    }

    /// <summary>
    /// Finds the block holding a data offset.
    /// </summary>
    /// <param name="dataOffset">An offset in the decompressed data stream.</param>
    /// <returns>The block index, or -1 when the offset is past the end.</returns>
    public int BlockAt(long dataOffset)
    {
        if (dataOffset < 0 || dataOffset >= DataLength)
        {
            return -1;
        }

        var index = Array.BinarySearch(DataStarts, dataOffset);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>
    /// Maps a range of the data stream to the file, when every block it touches is stored uncompressed.
    /// </summary>
    /// <param name="dataOffset">The start of the range in the data stream.</param>
    /// <param name="length">The range length.</param>
    /// <param name="fileOffset">The file offset of the range start.</param>
    /// <returns>True when the range can be edited in place in the file.</returns>
    // Uncompressed blocks are stored back to back with their compressed size equal to their size,
    // so a range that crosses from one into the next is still contiguous in the file.
    public bool TryMapUncompressed(long dataOffset, int length, out long fileOffset)
    {
        fileOffset = -1;
        var first = BlockAt(dataOffset);
        var last = BlockAt(dataOffset + length - 1);
        if (first < 0 || last < 0)
        {
            return false;
        }

        for (var index = first; index <= last; index++)
        {
            var block = Blocks[index];
            if ((block.Flags & 0x3F) != 0 || block.CompressedSize != block.DecompressedSize)
            {
                return false;
            }
        }

        fileOffset = FileStarts[first] + (dataOffset - DataStarts[first]);
        return true;
    }
}

/// <summary>
/// A read-only, seekable view of a bundle's decompressed data that decompresses LZ4 blocks on demand.
/// </summary>
internal sealed class BlockDataStream : Stream
{
    private const int CACHE_SIZE = 8;

    private readonly Stream _file;
    private readonly BlockLayout _layout;
    private readonly Dictionary<int, byte[]> _cache = [];
    private readonly Queue<int> _cacheOrder = new();

    /// <summary>
    /// Initializes the view.
    /// </summary>
    /// <param name="file">The bundle file stream.</param>
    /// <param name="layout">The block layout.</param>
    public BlockDataStream(Stream file, BlockLayout layout)
    {
        _file = file;
        _layout = layout;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _layout.DataLength;

    /// <inheritdoc />
    public override long Position { get; set; }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count && Position < Length)
        {
            var index = _layout.BlockAt(Position);
            var block = _layout.Blocks[index];
            var within = Position - _layout.DataStarts[index];
            var available = (int)Math.Min(count - total, block.DecompressedSize - within);

            if ((block.Flags & 0x3F) == 0)
            {
                _file.Position = _layout.FileStarts[index] + within;
                var read = _file.Read(buffer, offset + total, available);
                if (read == 0)
                {
                    throw new EndOfStreamException("The bundle file ended inside a data block.");
                }

                available = read;
            }
            else
            {
                Buffer.BlockCopy(Decompressed(index), (int)within, buffer, offset + total, available);
            }

            total += available;
            Position += available;
        }

        return total;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

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

    private byte[] Decompressed(int index)
    {
        if (_cache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var block = _layout.Blocks[index];
        _file.Position = _layout.FileStarts[index];
        var compressed = UnityFsBundle.ReadExactly(_file, (int)block.CompressedSize);
        var data = UnityFsBundle.DecodeLz4(compressed, (int)block.DecompressedSize);

        if (_cache.Count >= CACHE_SIZE)
        {
            _cache.Remove(_cacheOrder.Dequeue());
        }

        _cache[index] = data;
        _cacheOrder.Enqueue(index);
        return data;
    }
}

/// <summary>
/// A read-only stream that shows replacement bytes over chosen ranges of another stream.
/// </summary>
// The converter plans from the stock bytes even after a previous run edited a bundle in place, so
// it reads the edited file through this with each applied edit's old bytes laid back over it.
internal sealed class OverlayStream : Stream
{
    private readonly Stream _inner;
    private readonly IReadOnlyList<KeyValuePair<long, byte[]>> _patches;

    /// <summary>
    /// Initializes the overlay.
    /// </summary>
    /// <param name="inner">The stream to read through. The overlay takes ownership of it.</param>
    /// <param name="patches">File offsets and the bytes to show there instead.</param>
    public OverlayStream(Stream inner, IReadOnlyList<KeyValuePair<long, byte[]>> patches)
    {
        _inner = inner;
        _patches = patches;
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
        var start = _inner.Position;
        var read = _inner.Read(buffer, offset, count);
        var end = start + read;

        foreach (var patch in _patches)
        {
            var patchStart = patch.Key;
            var patchEnd = patch.Key + patch.Value.Length;
            if (patchEnd <= start || patchStart >= end)
            {
                continue;
            }

            var from = Math.Max(start, patchStart);
            var to = Math.Min(end, patchEnd);
            Buffer.BlockCopy(patch.Value, (int)(from - patchStart), buffer, offset + (int)(from - start), (int)(to - from));
        }

        return read;
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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
