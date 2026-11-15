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

    private static (Ksp2Patch Patch, MockFileSystem Fs, Mock<IZipArchiveEntry> MaliciousEntry) MakeMaliciousPatch(string maliciousFileName,
        int action = 1, string installDir = InstallDir)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(installDir);

        string manifestJson = $$"""
            {
              "operations": [
                {
                  "fileName": "{{maliciousFileName.Replace("\\", "\\\\")}}",
                  "action": {{action}},
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
    public async Task AsyncApply_EntryNameEscapesWithParentDirectorySegments_ThrowsAndNeverExtracts()
    {
        var (patch, fs, maliciousEntry) = MakeMaliciousPatch(@"..\..\..\evil.txt");
        var environmentProvider = new MockEnvironmentProvider();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await patch.AsyncApply(environmentProvider, InstallDir));

        // The containment check must reject the entry before any extraction is even attempted.
        maliciousEntry.Verify(e => e.ExtractToFile(It.IsAny<IFileSystem>(), It.IsAny<string>()), Times.Never);
        Assert.That(fs.File.Exists(@"C:\evil.txt"), Is.False);
    }

    // Action 2 is Remove. Each name resolves to the install folder itself, which Remove would delete whole.
    [TestCase(".")]
    [TestCase("")]
    [TestCase(@"KSP2_x64_Data\..")]
    public async Task AsyncApply_RemoveEntryNamingTheInstallFolder_ThrowsAndDeletesNothing(string name)
    {
        var (patch, fs, _) = MakeMaliciousPatch(name, action: 2);
        fs.File.WriteAllText(InstallDir + @"\uninstall.zip", "snapshot");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await patch.AsyncApply(new MockEnvironmentProvider(), InstallDir));

        Assert.That(fs.File.Exists(InstallDir + @"\uninstall.zip"), Is.True);
    }

    [Test]
    public async Task AsyncApply_InstallAtADriveRoot_AcceptsEntriesInsideIt()
    {
        var (patch, fs, _) = MakeMaliciousPatch(@"KSP2_x64_Data\old.dll", action: 2, installDir: @"D:\");
        fs.Directory.CreateDirectory(@"D:\KSP2_x64_Data");
        fs.File.WriteAllText(@"D:\KSP2_x64_Data\old.dll", "old");

        await patch.AsyncApply(new MockEnvironmentProvider(), @"D:\");

        Assert.That(fs.File.Exists(@"D:\KSP2_x64_Data\old.dll"), Is.False);
    }
}
