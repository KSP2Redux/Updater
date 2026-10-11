using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;
using InstallDistribution = Ksp2Redux.Tools.Launcher.Models.Distribution;

namespace Ksp2Redux.Tools.Launcher.Tests.Models;

/// <summary>
/// Revert to stock keeps the launcher's own folders under Redux, so they must not make a stock install look like a
/// Redux one. Otherwise the prepatch that follows refuses the install, and going back from a URP build to an earlier
/// one fails.
/// </summary>
public class Ksp2InstallTest
{
    private const string InstallDir = @"C:\Games\Ksp2";

    private static MockFileSystem MakeSteamInstall()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Plugins"));
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, Ksp2Install.KSP2_EXE_NAME), [0x00]);
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Plugins", "Steamworks.NET.txt"), "");
        return fs;
    }

    private static Ksp2Install Detect(MockFileSystem fs) =>
        new(fs, new Mock<IModuleDefinitionService>().Object, fs.Path.Combine(InstallDir, Ksp2Install.KSP2_EXE_NAME));

    [Test]
    public void ARevertedInstallKeepingTheConfigAndTheConversionJournalIsStock()
    {
        var fs = MakeSteamInstall();
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "Config"));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "BundleConversion", "StandaloneWindows64"));
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "Redux", "BundleConversion", "journal.json"), "{}");

        Assert.That(Detect(fs).Distribution, Is.EqualTo(InstallDistribution.Steam));
    }

    [Test]
    public void AnInstallWithReduxGameFilesIsRedux()
    {
        var fs = MakeSteamInstall();
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "BundleConversion"));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "ShaderBundles"));

        Assert.That(Detect(fs).Distribution, Is.EqualTo(InstallDistribution.Redux));
    }
}