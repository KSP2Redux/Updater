using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Mono.Cecil;
using Moq;
using Testably.Abstractions.Testing;
using InstallDistribution = Ksp2Redux.Tools.Launcher.Models.Distribution;

namespace Ksp2Redux.Tools.Launcher.Tests.Models;

/// <summary>
/// Revert to stock keeps the launcher's own folders under Redux, so they must not make a stock install look like a
/// Redux one. Otherwise the prepatch that follows refuses the install, and going back from a URP build to an earlier
/// one fails. The version has to be found in Redux builds from before and after the game moved out of
/// Assembly-CSharp, and stock game code put back under leftover Redux files has to read as stock.
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

    [Test]
    public void AReduxBuildWithTheGameInAssemblyCSharpIsReadFromIt()
    {
        var fs = MakeReduxInstall();
        var modules = new Mock<IModuleDefinitionService>();
        modules.Setup(m => m.ReadModule(ManagedPath(fs, "Assembly-CSharp.dll"))).Returns(ReduxVersionModule("Assembly-CSharp"));

        var install = Detect(fs, modules);

        Assert.That(install.GameVersion?.ToString(), Is.EqualTo(ReduxVersionText));
        Assert.That(install.Distribution, Is.EqualTo(InstallDistribution.Redux));
    }

    [Test]
    public void AReduxBuildWithTheGameInItsOwnAssemblyIsReadThroughTheForwarder()
    {
        var fs = MakeReduxInstall();
        var modules = new Mock<IModuleDefinitionService>();
        modules.Setup(m => m.ReadModule(ManagedPath(fs, "Assembly-CSharp.dll"))).Returns(ForwardingFacade("Ksp2Redux"));
        modules.Setup(m => m.ReadModule(ManagedPath(fs, "Ksp2Redux.dll"))).Returns(ReduxVersionModule("Ksp2Redux"));

        var install = Detect(fs, modules);

        Assert.That(install.VersionDetectionException, Is.Null);
        Assert.That(install.GameVersion?.ToString(), Is.EqualTo(ReduxVersionText));
        Assert.That(install.Distribution, Is.EqualTo(InstallDistribution.Redux));
    }

    [Test]
    public void StockGameCodeUnderLeftoverReduxFilesIsAStockInstall()
    {
        // A store file verification over a Redux install: the stock game code is back, the files Redux added stay.
        var fs = MakeReduxInstall();
        fs.File.WriteAllBytes(ManagedPath(fs, "Ksp2Redux.dll"), [0x00]);
        var modules = new Mock<IModuleDefinitionService>();
        modules.Setup(m => m.ReadModule(ManagedPath(fs, "Assembly-CSharp.dll"))).Returns(
            TestHelpers.GenerateMockVersionID(("VERSION_TEXT", "0.2.2.0.32914"), ("DEBUG_INFO", "BUILD_INFO")).module);

        var install = Detect(fs, modules);

        Assert.That(install.VersionDetectionException, Is.Null);
        Assert.That(install.GameVersion?.ToString(), Is.EqualTo("0.2.2.0.32914 (stable)"));
        Assert.That(install.Distribution, Is.EqualTo(InstallDistribution.Steam));
    }

    private const string ReduxVersionText = "0.2.9.0.105289 (internal)";

    private static MockFileSystem MakeReduxInstall()
    {
        var fs = MakeSteamInstall();
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "ShaderBundles"));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Managed"));
        return fs;
    }

    private static string ManagedPath(MockFileSystem fs, string assemblyFile) =>
        fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Managed", assemblyFile);

    private static Ksp2Install Detect(MockFileSystem fs, Mock<IModuleDefinitionService> modules) =>
        new(fs, modules.Object, fs.Path.Combine(InstallDir, Ksp2Install.KSP2_EXE_NAME));

    private static ModuleDefinition ReduxVersionModule(string assemblyName)
    {
        var module = TestHelpers.GenerateMockVersionID(
            ("VERSION_TEXT", "0.2.9.0.105289"),
            ("CHANNEL_NAME", "internal"),
            ("DEBUG_INFO", "abc1234")).module;
        module.Assembly.Name.Name = assemblyName;
        return module;
    }

    // What Redux builds after the repository consolidation ship as Assembly-CSharp: no types, only forwarders.
    private static ModuleDefinition ForwardingFacade(string gameAssemblyName)
    {
        var facade = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Assembly-CSharp", new Version(0, 0, 0, 0)), "Assembly-CSharp", ModuleKind.Dll).MainModule;
        var gameAssembly = new AssemblyNameReference(gameAssemblyName, new Version(0, 0, 0, 0));
        facade.AssemblyReferences.Add(gameAssembly);
        facade.ExportedTypes.Add(new ExportedType("", "VersionID", facade, gameAssembly) { Attributes = TypeAttributes.Forwarder });
        return facade;
    }
}