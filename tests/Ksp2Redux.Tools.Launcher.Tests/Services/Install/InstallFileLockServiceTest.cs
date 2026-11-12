using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// An install went ahead while a Unity editor held some of the game's bundles open. Those bundles failed partway
/// through, and the launcher still reported the install as finished. Any held game file has to stop the install
/// before it changes anything.
/// </summary>
public class InstallFileLockServiceTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string HeldBundle = @"KSP2_x64_Data\StreamingAssets\aa\StandaloneWindows64\ui_assets_all.bundle";

    private static MockFileSystem MakeInstall()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, @"KSP2_x64_Data\StreamingAssets\aa\StandaloneWindows64"));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "mods"));
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, "KSP2_x64.exe"), [0x00]);
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, HeldBundle), [0x00]);
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, @"mods\notes.txt"), [0x00]);
        return fs;
    }

    private static InstallFileLockService MakeService(MockFileSystem fs)
    {
        var operatingSystem = new Mock<IOperatingSystemService>();
        operatingSystem.Setup(os => os.IsWindows()).Returns(false);
        return new InstallFileLockService(fs, operatingSystem.Object);
    }

    [Test]
    public void ABundleAnotherProgramHoldsOpenStopsTheInstall()
    {
        var fs = MakeInstall();
        using var editor = fs.File.Open(fs.Path.Combine(InstallDir, HeldBundle), FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Assert.Throws<FilesInUseException>(() => MakeService(fs).EnsureNoFilesInUse(InstallDir));

        Assert.That(ex!.Files, Is.EqualTo(new[] { HeldBundle }));
        Assert.That(ex.Message, Does.Contain(HeldBundle));
    }

    [Test]
    public void NoHeldFilesLetTheInstallGoAhead()
    {
        var fs = MakeInstall();

        Assert.DoesNotThrow(() => MakeService(fs).EnsureNoFilesInUse(InstallDir));
    }

    [Test]
    public void AFileHeldInTheModsFolderDoesNotStopTheInstall()
    {
        var fs = MakeInstall();
        using var editor = fs.File.Open(fs.Path.Combine(InstallDir, @"mods\notes.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.DoesNotThrow(() => MakeService(fs).EnsureNoFilesInUse(InstallDir));
    }
}