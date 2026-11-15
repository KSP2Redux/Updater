using AssetsTools.NET;
using LZ4ps;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

/// <summary>
/// Writes a converted copy of a stock bundle: externals added, PPtrs retargeted, LZ4HC compressed.
/// </summary>
internal static class BundleRewriter
{
    /// <summary>
    /// Unity's LZ4 chunk size. Matching it keeps the copy's random access behaviour the same as stock.
    /// </summary>
    private const int BLOCK_SIZE = 128 * 1024;

    // LZ4HC, as stock uses. Measured on the test set it lands within 1% of the stock sizes, where
    // plain LZ4 came out 30% larger for 15% less time.
    private const ushort LZ4HC_BLOCK = 3;
    private const ushort UNCOMPRESSED_BLOCK = 0;

    /// <summary>
    /// Writes the converted copy.
    /// </summary>
    /// <param name="stock">The stock bundle, read from the start. The rewriter takes ownership of it.</param>
    /// <param name="plan">The bundle's edits.</param>
    /// <param name="output">A writable, seekable, empty stream for the copy.</param>
    /// <exception cref="InvalidDataException">The stock bytes do not match what the plan expects.</exception>
    public static void Write(Stream stock, BundleEditPlan plan, Stream output)
    {
        using var bundle = UnityFsBundle.Open(stock);
        var editsByFile = plan.Edits
            .GroupBy(edit => edit.SerializedFile, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        List<KeyValuePair<AssetBundleDirectoryInfo, Stream>> entries = [];
        foreach (var entry in bundle.Directory)
        {
            var source = new SegmentStream(bundle.Data, entry.Offset, entry.DecompressedSize);
            editsByFile.TryGetValue(entry.Name, out var edits);
            plan.AddedExternals.TryGetValue(entry.Name, out var externals);
            if (edits is null && externals is null)
            {
                entries.Add(new KeyValuePair<AssetBundleDirectoryInfo, Stream>(entry, source));
                continue;
            }

            entries.Add(new KeyValuePair<AssetBundleDirectoryInfo, Stream>(
                entry,
                RewriteSerializedFile(source, entry, edits ?? [], externals ?? [])));
        }

        var missing = editsByFile.Keys.Where(name => bundle.Directory.All(entry => entry.Name != name)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException($"The plan edits {string.Join(", ", missing)}, which the bundle does not contain.");
        }

        WriteBundle(output, bundle.Header, entries, compress: true);
    }

    private static MemoryStream RewriteSerializedFile(
        Stream source,
        AssetBundleDirectoryInfo entry,
        IReadOnlyList<PlannedEdit> edits,
        IReadOnlyList<string> addedExternals)
    {
        var file = new AssetsFile();
        file.Read(new AssetsFileReader(source));
        file.GenerateQuickLookup();

        // Appended rather than inserted, so every file ID already in the file keeps its meaning.
        foreach (var cab in addedExternals)
        {
            var path = $"archive:/{cab}/{cab}";
            file.Metadata.Externals.Add(new AssetsFileExternal
            {
                VirtualAssetPathName = "",
                Guid = default,
                Type = AssetsFileExternalType.Normal,
                PathName = path,
                OriginalPathName = path,
            });
        }

        foreach (var group in edits.GroupBy(edit => edit.ObjectPathId))
        {
            var info = file.GetAssetInfo(group.Key)
                       ?? throw new InvalidDataException($"{entry.Name} has no object {group.Key}.");
            var start = info.GetAbsoluteByteOffset(file);
            file.Reader.Position = start;
            var bytes = file.Reader.ReadBytes((int)info.ByteSize);

            foreach (var edit in group)
            {
                var offset = edit.DataOffset - entry.Offset - start;
                if (offset < 0 || offset + Hex.PPTR_SIZE > bytes.Length)
                {
                    throw new InvalidDataException($"Edit at data offset {edit.DataOffset} is outside object {group.Key}.");
                }

                if (!bytes.AsSpan((int)offset, Hex.PPTR_SIZE).SequenceEqual(edit.Old))
                {
                    throw new InvalidDataException(
                        $"{entry.Name} object {group.Key} {edit.FieldPath} holds {Hex.Encode(bytes.AsSpan((int)offset, Hex.PPTR_SIZE))}, the plan expected {Hex.Encode(edit.Old)}.");
                }

                Buffer.BlockCopy(edit.New, 0, bytes, (int)offset, Hex.PPTR_SIZE);
            }

            info.SetNewData(bytes);
        }

        MemoryStream rewritten = new();
        file.Write(new AssetsFileWriter(rewritten));
        rewritten.Position = 0;
        return rewritten;
    }

    /// <summary>
    /// Writes a UnityFS bundle from a list of entries.
    /// </summary>
    /// <param name="output">A writable, seekable, empty stream.</param>
    /// <param name="source">The header to copy the format and Unity version from.</param>
    /// <param name="entries">The directory entries, with streams holding their contents.</param>
    /// <param name="compress">True for LZ4HC blocks, false to store every block uncompressed.</param>
    /// <param name="useK4os">True for standalone pinned HC6 encoding, false for legacy AssetsTools encoding.</param>
    /// <param name="workers">Worker count from one to 64, or zero to use at most 32 logical cores.</param>
    // Laid out the way Unity writes bundles: header, block list at the front padded to 16 bytes,
    // then 128 KiB blocks. The block list itself is stored uncompressed so its size is known before
    // the data is written, which lets it be patched in place afterwards instead of buffering the data.
    internal static void WriteBundle(
        Stream output,
        AssetBundleHeader source,
        IReadOnlyList<KeyValuePair<AssetBundleDirectoryInfo, Stream>> entries,
        bool compress, bool useK4os = false, int workers = 0)
    {
        long total = 0;
        List<AssetBundleDirectoryInfo> directory = [];
        foreach (var entry in entries)
        {
            directory.Add(new AssetBundleDirectoryInfo
            {
                Offset = total,
                DecompressedSize = entry.Value.Length,
                Flags = entry.Key.Flags,
                Name = entry.Key.Name,
            });
            total += entry.Value.Length;
        }

        var blockCount = (int)((total + BLOCK_SIZE - 1) / BLOCK_SIZE);
        var blocks = new AssetBundleBlockInfo[blockCount];
        for (var index = 0; index < blockCount; index++)
        {
            blocks[index] = new AssetBundleBlockInfo
            {
                DecompressedSize = (uint)Math.Min(BLOCK_SIZE, total - (long)index * BLOCK_SIZE),
            };
        }

        AssetBundleBlockAndDirInfo info = new()
        {
            Hash = new Hash128(new byte[16]),
            BlockInfos = blocks,
            DirectoryInfos = directory,
        };

        var header = new AssetBundleHeader
        {
            Signature = "UnityFS",
            Version = source.Version,
            GenerationVersion = source.GenerationVersion,
            EngineVersion = source.EngineVersion,
            FileStreamHeader = new AssetBundleFSHeader
            {
                Flags = AssetBundleFSHeaderFlags.HasDirectoryInfo | AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart,
            },
        };

        var infoBytes = Serialize(info);
        header.FileStreamHeader.CompressedSize = (uint)infoBytes.Length;
        header.FileStreamHeader.DecompressedSize = (uint)infoBytes.Length;

        var writer = new AssetsFileWriter(output);
        header.Write(writer);
        if (header.Version >= 7)
        {
            writer.Align16();
        }

        writer.Write(infoBytes);
        writer.Align16();
        if (writer.Position != header.GetFileDataOffset())
        {
            throw new InvalidOperationException("The bundle data offset does not match the header layout.");
        }

        // Read shared source streams serially. Only independent block encoding runs in parallel.
        int concurrency = workers > 0 ? workers : Math.Min(Environment.ProcessorCount, 32);
        if (concurrency < 1 || concurrency > 64)
            throw new ArgumentOutOfRangeException(nameof(workers));
        var pending = new Queue<(byte[] Bytes, AssetBundleBlockInfo Block)>();
        void FlushBlocks()
        {
            var batch = pending.ToArray();
            var encoded = new byte[batch.Length][];
            Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                index => encoded[index] = Encode(batch[index].Bytes, batch[index].Bytes.Length, useK4os));
            for (int index = 0; index < batch.Length; index++)
            {
                WriteEncoded(output, batch[index].Bytes, batch[index].Bytes.Length, batch[index].Block, encoded[index]);
            }
            pending.Clear();
        }
        void Emit(byte[] bytes, int length, AssetBundleBlockInfo block)
        {
            if (!compress || !useK4os)
            {
                WriteBlock(output, bytes, length, block, compress);
                return;
            }
            var copy = new byte[length];
            Buffer.BlockCopy(bytes, 0, copy, 0, length);
            pending.Enqueue((copy, block));
            if (pending.Count == concurrency * 2)
                FlushBlocks();
        }
        var buffer = new byte[BLOCK_SIZE];
        var filled = 0;
        var blockIndex = 0;
        foreach (var entry in entries)
        {
            var content = entry.Value;
            content.Position = 0;
            int read;
            while ((read = content.Read(buffer, filled, BLOCK_SIZE - filled)) > 0)
            {
                filled += read;
                if (filled == BLOCK_SIZE)
                {
                    Emit(buffer, filled, blocks[blockIndex++]);
                    filled = 0;
                }
            }
        }

        if (filled > 0)
        {
            Emit(buffer, filled, blocks[blockIndex++]);
        }

        FlushBlocks();

        if (blockIndex != blockCount)
        {
            throw new InvalidOperationException($"Wrote {blockIndex} blocks, expected {blockCount}.");
        }

        header.FileStreamHeader.TotalFileSize = output.Position;
        var rewrittenInfo = Serialize(info);
        if (rewrittenInfo.Length != infoBytes.Length)
        {
            throw new InvalidOperationException("The block list changed size between passes.");
        }

        output.Position = 0;
        header.Write(writer);
        if (header.Version >= 7)
        {
            writer.Align16();
        }

        writer.Write(rewrittenInfo);
        output.Position = header.FileStreamHeader.TotalFileSize;
        output.Flush();
    }

    private static void WriteBlock(Stream output, byte[] buffer, int length, AssetBundleBlockInfo block, bool compress)
    {
        var compressed = compress ? Encode(buffer, length, false) : [];
        WriteEncoded(output, buffer, length, block, compressed);
    }

    private static byte[] Encode(byte[] bytes, int length, bool useK4os)
    {
        if (!useK4os)
            return LZ4Codec.Encode32HC(bytes, 0, length);
#if UNITY_EDITOR
        throw new NotSupportedException("HC6 publication requires the standalone .NET tools.");
#else
        var result = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(length)];
        int size = K4os.Compression.LZ4.LZ4Codec.Encode(bytes, 0, length, result, 0, result.Length,
            K4os.Compression.LZ4.LZ4Level.L06_HC);
        if (size <= 0)
            throw new InvalidDataException("HC6 block encoding failed.");
        Array.Resize(ref result, size);
        return result;
#endif
    }

    private static void WriteEncoded(Stream output, byte[] buffer, int length, AssetBundleBlockInfo block, byte[] compressed)
    {
        if (compressed.Length > 0 && compressed.Length < length)
        {
            output.Write(compressed, 0, compressed.Length);
            block.CompressedSize = (uint)compressed.Length;
            block.Flags = LZ4HC_BLOCK;
        }
        else
        {
            output.Write(buffer, 0, length);
            block.CompressedSize = (uint)length;
            block.Flags = UNCOMPRESSED_BLOCK;
        }

        block.DecompressedSize = (uint)length;
    }

    private static byte[] Serialize(AssetBundleBlockAndDirInfo info)
    {
        using MemoryStream stream = new();
        info.Write(new AssetsFileWriter(stream) { BigEndian = true });
        return stream.ToArray();
    }
}
