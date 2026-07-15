using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Wrappers;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests;

public class Ksp2PatchIdempotentRetryTest
{
    private const string InstallDir = @"C:\Games\Ksp2";

    [Test]
    public async Task AsyncApply_FileAlreadyMatchesExpectedResult_SkipsWithoutTouchingArchiveEntry()
    {
        // Arrange - simulate retrying a plan after a previous attempt already finished this one file
        // (e.g. a different operation in the same step failed and the plan is being retried).
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);

        byte[] alreadyAppliedContent = "already the correct final content"u8.ToArray();
        string finalHashB64 = Convert.ToBase64String(SHA256.HashData(alreadyAppliedContent));
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, "already-done.txt"), alreadyAppliedContent);

        string manifestJson = $$"""
            {
              "operations": [
                {
                  "fileName": "already-done.txt",
                  "action": 1,
                  "originalHash": null,
                  "finalHash": "{{finalHashB64}}"
                }
              ]
            }
            """;

        var archive = new Mock<IZipArchive>();
        archive.SetupGet(a => a.Mode).Returns(ZipArchiveMode.Read);

        var manifestEntry = new Mock<IZipArchiveEntry>();
        manifestEntry.Setup(e => e.Open()).Returns(new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)));
        archive.Setup(a => a.GetEntry("manifest.json")).Returns(manifestEntry.Object);

        var fileEntry = new Mock<IZipArchiveEntry>();
        archive.Setup(a => a.GetEntry("already-done.txt")).Returns(fileEntry.Object);

        var patch = new Ksp2Patch(fs, archive.Object);
        var environmentProvider = new MockEnvironmentProvider();

        // Act
        await patch.AsyncApply(environmentProvider, InstallDir);

        // Assert - the file was recognized as already matching the expected result and left alone,
        // rather than being reopened/rewritten by the (redundant) copy-from-patch step.
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(InstallDir, "already-done.txt")), Is.EqualTo(alreadyAppliedContent));
    }
}
