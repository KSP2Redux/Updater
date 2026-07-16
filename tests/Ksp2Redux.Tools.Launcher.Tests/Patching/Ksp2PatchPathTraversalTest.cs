using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Wrappers;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Patching;

public class Ksp2PatchPathTraversalTest
{
    private const string InstallDir = @"C:\Games\Ksp2";

    private static (Ksp2Patch Patch, MockFileSystem Fs, Mock<IZipArchiveEntry> MaliciousEntry) MakeMaliciousPatch(string maliciousFileName)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);

        string manifestJson = $$"""
            {
              "operations": [
                {
                  "fileName": "{{maliciousFileName.Replace("\\", "\\\\")}}",
                  "action": 1,
                  "originalHash": null,
                  "finalHash": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                }
              ]
            }
            """;

        var archive = new Mock<IZipArchive>();
        archive.SetupGet(a => a.Mode).Returns(ZipArchiveMode.Read);

        var manifestEntry = new Mock<IZipArchiveEntry>();
        manifestEntry.Setup(e => e.Open()).Returns(new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)));
        archive.Setup(a => a.GetEntry("manifest.json")).Returns(manifestEntry.Object);

        var maliciousEntry = new Mock<IZipArchiveEntry>();
        archive.Setup(a => a.GetEntry(maliciousFileName)).Returns(maliciousEntry.Object);

        var patch = new Ksp2Patch(fs, archive.Object);
        return (patch, fs, maliciousEntry);
    }

    [Test]
    public void AsyncApply_EntryNameEscapesWithParentDirectorySegments_ThrowsAndNeverExtracts()
    {
        var (patch, fs, maliciousEntry) = MakeMaliciousPatch(@"..\..\..\evil.txt");
        var environmentProvider = new MockEnvironmentProvider();

        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await patch.AsyncApply(environmentProvider, InstallDir));

        // The containment check must reject the entry before any extraction is even attempted.
        maliciousEntry.Verify(e => e.ExtractToFile(It.IsAny<IFileSystem>(), It.IsAny<string>()), Times.Never);
        Assert.That(fs.File.Exists(@"C:\evil.txt"), Is.False);
    }
}
