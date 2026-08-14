using System.Security.Cryptography;
using Ksp2Redux.Tools.Common.Distribution;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Distribution;

public class PatchChunkerTest
{
    [TestCase(7, 8, 1)]
    [TestCase(8, 8, 1)]
    [TestCase(9, 8, 2)]
    [TestCase(25, 8, 4)]
    public async Task SplitAsync_ProducesOrderedHashVerifiedParts(int inputSize, int chunkSize, int expectedParts)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("/input");
        fileSystem.Directory.CreateDirectory("/parts");
        byte[] bytes = Enumerable.Range(0, inputSize).Select(value => (byte)value).ToArray();
        fileSystem.File.WriteAllBytes("/input/release.patch", bytes);

        var packaged = await PatchChunker.SplitAsync(
            fileSystem, "/input/release.patch", "/parts", chunkSize);

        Assert.Multiple(() =>
        {
            Assert.That(packaged.Size, Is.EqualTo(inputSize));
            Assert.That(packaged.Chunks, Has.Count.EqualTo(expectedParts));
            Assert.That(packaged.ChecksumSha256,
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
            Assert.That(packaged.Chunks.Select(chunk => chunk.Index),
                Is.EqualTo(Enumerable.Range(0, expectedParts)));
            Assert.That(packaged.Chunks.All(chunk => chunk.Size <= chunkSize), Is.True);
        });

        byte[] reconstructed = packaged.Chunks
            .SelectMany(chunk => fileSystem.File.ReadAllBytes(chunk.Path))
            .ToArray();
        Assert.That(reconstructed, Is.EqualTo(bytes));
        foreach (var chunk in packaged.Chunks)
        {
            Assert.That(chunk.ChecksumSha256,
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(fileSystem.File.ReadAllBytes(chunk.Path)))));
        }
    }

    [Test]
    public void DefaultChunkSize_Is480MiB()
    {
        Assert.That(PatchChunker.DEFAULT_CHUNK_SIZE, Is.EqualTo(503_316_480));
    }
}
