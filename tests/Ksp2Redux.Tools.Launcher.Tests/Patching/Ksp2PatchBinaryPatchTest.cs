using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BsDiff;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Wrappers;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Patching;

public class Ksp2PatchBinaryPatchTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string FileName = "game.dll";

    private static readonly byte[] Original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("stock game code ", 200)));
    private static readonly byte[] Patched = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("redux game code! ", 200)));

    private static (Ksp2Patch Patch, MockFileSystem Fs) MakePatch()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetTempPath());

        using var diff = new MemoryStream();
        BinaryPatch.Create(Original, Patched, diff);
        byte[] diffBytes = diff.ToArray();

        string manifestJson = $$"""
            {
              "operations": [
                {
                  "fileName": "{{FileName}}",
                  "action": 0,
                  "originalHash": "{{Convert.ToBase64String(SHA256.HashData(Original))}}",
                  "finalHash": "{{Convert.ToBase64String(SHA256.HashData(Patched))}}"
                }
              ]
            }
            """;

        var archive = new Mock<IZipArchive>();
        archive.SetupGet(a => a.Mode).Returns(ZipArchiveMode.Read);
        var manifestEntry = new Mock<IZipArchiveEntry>();
        manifestEntry.Setup(e => e.Open()).Returns(new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)));
        archive.Setup(a => a.GetEntry("manifest.json")).Returns(manifestEntry.Object);
        var diffEntry = new Mock<IZipArchiveEntry>();
        diffEntry.Setup(e => e.ExtractToFile(It.IsAny<IFileSystem>(), It.IsAny<string>()))
            .Callback<IFileSystem, string>((fileSystem, path) => fileSystem.File.WriteAllBytes(path, diffBytes));
        archive.Setup(a => a.GetEntry($"{FileName}.bsdiff")).Returns(diffEntry.Object);

        return (new Ksp2Patch(fs, archive.Object), fs);
    }

    [Test]
    public async Task AsyncApply_StockFile_IsPatchedAndLeavesNoTemporaryFiles()
    {
        // Arrange
        var (patch, fs) = MakePatch();
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, FileName), Original);

        // Act
        await patch.AsyncApply(new MockEnvironmentProvider(), InstallDir);

        // Assert
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(InstallDir, FileName)), Is.EqualTo(Patched));
        Assert.That(fs.Directory.EnumerateFiles(InstallDir), Is.EqualTo(new[] { fs.Path.Combine(InstallDir, FileName) }));
    }

    [Test]
    public async Task AsyncApply_FileDoesNotMatchTheOriginalHash_LeavesNoTemporaryFileInTheInstall()
    {
        // Arrange
        var (patch, fs) = MakePatch();
        byte[] modded = Encoding.UTF8.GetBytes("someone else's build");
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, FileName), modded);

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => patch.AsyncApply(new MockEnvironmentProvider(), InstallDir));

        // Assert
        Assert.That(fs.Directory.EnumerateFiles(InstallDir), Is.EqualTo(new[] { fs.Path.Combine(InstallDir, FileName) }));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(InstallDir, FileName)), Is.EqualTo(modded));
    }
}
