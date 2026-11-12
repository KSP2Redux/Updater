using AssetsTools.NET;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

public partial class BundleRecipe
{
    /// <summary>
    /// Validate the complete persisted recipe structure without opening source or payload streams.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when versions, identities, entries, segments or ranges are invalid.
    /// </exception>
    public void ValidateStructure() => Validate(Sources?.Count ?? 0, PayloadLength);

    /// <summary>
    /// Preflight UnityFS metadata and storage blocks before the normal reader allocates its caches.
    /// </summary>
    /// <remarks>
    /// This release preparation profile accepts at most eight MiB of block metadata, one MiB per
    /// compressed storage block, 65536 blocks and directory entries, 4096 bytes per entry name and eight GiB of
    /// decompressed data. Raw blocks stream directly and may contain up to int.MaxValue bytes with equal
    /// stored and decompressed sizes. At most eight compressed blocks are cached by each existing reader. Raw files need
    /// no preflight. The input remains open and its position is restored. This does not hash data.
    /// </remarks>
    /// <param name="input">The stable readable, seekable native UnityFS stream.</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the container is malformed or exceeds this profile's supported bounds.
    /// </exception>
    public static void ValidateReleaseInputBounds(Stream input)
    {
        RequireInput(input, nameof(input));
        long position = input.Position;
        try
        {
            input.Position = 0;
            RequireTerminatedString(input, 8);
            SkipExactly(input, 4);
            RequireTerminatedString(input, 256);
            RequireTerminatedString(input, 256);
            input.Position = 0;
            var reader = new AssetsFileReader(input);
            var header = new AssetBundleHeader();
            header.Read(reader);
            const uint metadataLimit = 8 * 1024 * 1024;
            if (header.Signature != "UnityFS" || header.Version is < 6 or > 8 ||
                header.FileStreamHeader.TotalFileSize != input.Length ||
                header.FileStreamHeader.CompressedSize > metadataLimit ||
                header.FileStreamHeader.DecompressedSize > metadataLimit)
                throw new InvalidDataException("UnityFS header exceeds release preparation bounds.");
            long offset = header.GetBundleInfoOffset();
            int storedSize = checked((int)header.FileStreamHeader.CompressedSize);
            int dataSize = checked((int)header.FileStreamHeader.DecompressedSize);
            ValidateRange(offset, storedSize, input.Length);
            input.Position = offset;
            byte[] stored = UnityFsBundle.ReadExactly(input, storedSize);
            uint compression = (uint)header.FileStreamHeader.Flags & 63;
            byte[] metadata = compression switch
            {
                0 when storedSize == dataSize => stored,
                2 or 3 => UnityFsBundle.DecodeLz4(stored, dataSize),
                _ => throw new InvalidDataException("Unsupported UnityFS metadata compression."),
            };
            using var bytes = new MemoryStream(metadata, false);
            var info = new AssetsFileReader(bytes) { BigEndian = true };
            SkipExactly(bytes, 16);
            uint blocks = info.ReadUInt32();
            if (blocks > 65536 || blocks > (metadata.Length - bytes.Position) / 10)
                throw new InvalidDataException("UnityFS block count exceeds its bounded metadata.");
            long total = 0;
            long storedTotal = 0;
            for (uint index = 0; index < blocks; index++)
            {
                uint length = info.ReadUInt32();
                uint compressed = info.ReadUInt32();
                ushort flags = info.ReadUInt16();
                int blockCompression = flags & 63;
                if (length is 0 or > int.MaxValue || compressed is 0 or > int.MaxValue ||
                    blockCompression is not (0 or 2 or 3) ||
                    (blockCompression == 0 && length != compressed) ||
                    (blockCompression != 0 && (length > 1024 * 1024 || compressed > 1024 * 1024)))
                    throw new InvalidDataException("UnityFS storage block exceeds release preparation bounds.");
                total += length;
                storedTotal += compressed;
            }
            if (total > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("UnityFS data exceeds the eight GiB preparation bound.");
            long dataOffset = header.GetFileDataOffset();
            long dataEnd = ((uint)header.FileStreamHeader.Flags & 128) != 0 ? offset : input.Length;
            ValidateRange(dataOffset, storedTotal, dataEnd);
            uint entries = info.ReadUInt32();
            if (entries > 65536 || entries > (metadata.Length - bytes.Position) / 21)
                throw new InvalidDataException("UnityFS directory count exceeds release preparation bounds.");
            for (uint index = 0; index < entries; index++)
            {
                long start = info.ReadInt64();
                long length = info.ReadInt64();
                info.ReadUInt32();
                ValidateRange(start, length, total);
                RequireTerminatedString(bytes, 4096);
            }
            if (bytes.Position != bytes.Length)
                throw new InvalidDataException("Unexpected UnityFS block metadata tail.");
        }
        finally
        {
            input.Position = position;
        }
    }

    private static void RequireTerminatedString(Stream input, int bound)
    {
        for (int index = 0; index < bound; index++)
        {
            int value = input.ReadByte();
            if (value < 0)
                throw new EndOfStreamException();
            if (value == 0)
                return;
        }
        throw new InvalidDataException("UnityFS string exceeds release preparation bounds.");
    }

    private static void SkipExactly(Stream input, int count)
    {
        ValidateRange(input.Position, count, input.Length);
        input.Position += count;
    }
}
