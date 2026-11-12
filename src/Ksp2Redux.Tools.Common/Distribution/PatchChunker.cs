using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;

namespace Ksp2Redux.Tools.Common.Distribution;

/// <summary>
/// Splits a patch byte stream into deterministic transport parts.
/// </summary>
public static class PatchChunker
{
    /// <summary>
    /// The production transport-part size in bytes.
    /// </summary>
    public const long DEFAULT_CHUNK_SIZE = 480L * 1024 * 1024;

    /// <summary>
    /// Splits a patch into ordered parts and calculates all checksums.
    /// </summary>
    /// <param name="fileSystem">The file system used for input and output.</param>
    /// <param name="patchPath">The source patch path.</param>
    /// <param name="outputDirectory">The directory that receives the parts.</param>
    /// <param name="chunkSize">The maximum part size in bytes.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The packaged patch description.</returns>
    public static async Task<PackagedPatch> SplitAsync(
        IFileSystem fileSystem,
        string patchPath,
        string outputDirectory,
        long chunkSize = DEFAULT_CHUNK_SIZE,
        CancellationToken ct = default)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (!fileSystem.File.Exists(patchPath))
            throw new FileNotFoundException("The patch to package does not exist.", patchPath);

        long patchSize = fileSystem.FileInfo.New(patchPath).Length;
        if (patchSize <= 0)
            throw new InvalidDataException("An empty patch cannot be packaged.");

        fileSystem.Directory.CreateDirectory(outputDirectory);
        var chunks = new List<PackagedPatchChunk>();
        using var wholeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = fileSystem.FileStream.New(
            patchPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long remaining = patchSize;
            int index = 0;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                long expectedPartSize = Math.Min(chunkSize, remaining);
                string partPath = fileSystem.Path.Combine(outputDirectory, $"part-{index:D4}.bin");
                using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var output = fileSystem.FileStream.New(
                    partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);

                long partSize = 0;
                while (partSize < expectedPartSize)
                {
                    int requested = (int)Math.Min(buffer.Length, expectedPartSize - partSize);
                    int read = await input.ReadAsync(buffer.AsMemory(0, requested), ct);
                    if (read == 0)
                        throw new EndOfStreamException("The patch ended before the expected number of bytes was read.");

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    partHash.AppendData(buffer, 0, read);
                    wholeHash.AppendData(buffer, 0, read);
                    partSize += read;
                }

                await output.FlushAsync(ct);
                chunks.Add(new PackagedPatchChunk(index, partPath, partSize,
                    Convert.ToHexString(partHash.GetHashAndReset())));
                remaining -= partSize;
                index++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new PackagedPatch(
            patchPath,
            patchSize,
            Convert.ToHexString(wholeHash.GetHashAndReset()),
            chunks);
    }
}

/// <summary>
/// A patch and its packaged transport parts.
/// </summary>
/// <param name="PatchPath">The source patch path.</param>
/// <param name="Size">The complete patch size in bytes.</param>
/// <param name="ChecksumSha256">The complete patch SHA-256 checksum.</param>
/// <param name="Chunks">The ordered transport parts.</param>
public sealed record PackagedPatch(
    string PatchPath,
    long Size,
    string ChecksumSha256,
    IReadOnlyList<PackagedPatchChunk> Chunks);

/// <summary>
/// A local transport part produced from a patch.
/// </summary>
/// <param name="Index">The zero-based part index.</param>
/// <param name="Path">The local part path.</param>
/// <param name="Size">The part size in bytes.</param>
/// <param name="ChecksumSha256">The part SHA-256 checksum.</param>
public sealed record PackagedPatchChunk(int Index, string Path, long Size, string ChecksumSha256);
