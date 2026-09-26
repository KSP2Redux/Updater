using EnvironmentAbstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Mac;

public class WineRuntimeServiceTest
{
    private const string APP_EXE = "/Applications/KSP2 Redux.app/Contents/MacOS/Ksp2Redux.Tools.Launcher";
    private const string BUNDLED_ROOT = "/Applications/KSP2 Redux.app/Contents/Resources/wine-runtime";
    private const string STORAGE = "/Users/player/Library/Application Support/Ksp2Redux";
    private const string CROSSOVER_WINE = "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine";

    private static WineRuntimeService Build(MockFileSystem fileSystem, Mock<IProcessRunner>? processRunner = null,
        string processPath = APP_EXE, LauncherConfig? launcherConfig = null)
    {
        var environment = new Mock<IEnvironmentProvider>();
        environment.Setup(e => e.ProcessPath).Returns(processPath);
        environment.Setup(e => e.UserName).Returns("player");
        environment.Setup(e => e.GetFolderPath(Environment.SpecialFolder.UserProfile)).Returns("/Users/player");

        var config = new Mock<ILauncherConfigService>();
        config.Setup(c => c.GetLocalStorageDirectory()).Returns(STORAGE);
        config.Setup(c => c.Config).Returns(launcherConfig ?? new LauncherConfig());

        return new WineRuntimeService(fileSystem, environment.Object, config.Object,
            (processRunner ?? new Mock<IProcessRunner>()).Object, new Mock<ILogService>().Object);
    }

    private static MockFileSystem NewMacFileSystem() => new(o => o.SimulatingOperatingSystem(SimulationMode.MacOS));

    private static void AddBundledRuntime(MockFileSystem fileSystem, string root = BUNDLED_ROOT)
    {
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(root, "wine", "bin"));
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(root, "wine", "bin", "wine"), "");
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(root, "runtime.json"),
            """{"wine":"Wine 10.10 + DXMT","dxmt":"v0.80","wineBinary":"wine/bin/wine","prefixEnv":{"WINEDLLOVERRIDES":"mscoree,mshtml="}}""");
    }

    private static void AddCrossOver(MockFileSystem fileSystem)
    {
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(CROSSOVER_WINE)!);
        fileSystem.File.WriteAllText(CROSSOVER_WINE, "");
    }

    [Test]
    public void Detect_RuntimeInsideTheApp_IsPreferredOverCrossOver()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        AddCrossOver(fileSystem);

        // Act
        var runtime = Build(fileSystem).Detect();

        // Assert
        Assert.That(runtime, Is.Not.Null);
        Assert.That(runtime!.Kind, Is.EqualTo(WineRuntimeKind.Bundled));
        Assert.That(runtime.WineBinary, Is.EqualTo(fileSystem.Path.Combine(BUNDLED_ROOT, "wine", "bin", "wine")));
        Assert.That(runtime.PrefixPath, Is.EqualTo(fileSystem.Path.Combine(STORAGE, "wine-prefix")));
        Assert.That(runtime.DisplayName, Is.EqualTo("Wine 10.10 + DXMT"));
    }

    [Test]
    public void Detect_RuntimeInStorageFolder_IsFound()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem, fileSystem.Path.Combine(STORAGE, "wine-runtime"));

        // Act
        var runtime = Build(fileSystem).Detect();

        // Assert
        Assert.That(runtime?.Kind, Is.EqualTo(WineRuntimeKind.Bundled));
    }

    [Test]
    public void Detect_OnlyCrossOver_UsesTheReduxBottle()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddCrossOver(fileSystem);

        // Act
        var runtime = Build(fileSystem).Detect();

        // Assert
        Assert.That(runtime?.Kind, Is.EqualTo(WineRuntimeKind.CrossOver));
        Assert.That(runtime!.PrefixPath,
            Is.EqualTo("/Users/player/Library/Application Support/CrossOver/Bottles/KSP2Redux"));
    }

    [Test]
    public void Detect_ManifestWithoutWineBinary_IsIgnored()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        fileSystem.Directory.CreateDirectory(BUNDLED_ROOT);
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(BUNDLED_ROOT, "runtime.json"), """{"wineBinary":"wine/bin/wine"}""");

        // Act
        var runtime = Build(fileSystem).Detect();

        // Assert
        Assert.That(runtime, Is.Null);
    }

    [Test]
    public void CreateLaunchInfo_CrossOver_RunsTheExeInTheBottle()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddCrossOver(fileSystem);
        var service = Build(fileSystem);
        var runtime = service.Detect()!;

        // Act
        var info = service.CreateLaunchInfo(runtime, "/Games/KSP2/KSP2_x64.exe", "/Games/KSP2", "-popupwindow");

        // Assert
        Assert.That(info.FileName, Is.EqualTo(CROSSOVER_WINE));
        Assert.That(info.ArgumentList, Is.EqualTo(new[]
        {
            "--bottle", "KSP2Redux", "--workdir", "/Games/KSP2", "--", "/Games/KSP2/KSP2_x64.exe", "-popupwindow"
        }));
    }

    [Test]
    public void CreateLaunchInfo_Bundled_PointsWineAtTheReduxPrefix()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        var service = Build(fileSystem);
        var runtime = service.Detect()!;

        // Act
        var info = service.CreateLaunchInfo(runtime, "/Games/KSP2/KSP2_x64.exe", "/Games/KSP2", null);

        // Assert
        Assert.That(info.ArgumentList, Is.EqualTo(new[] { "/Games/KSP2/KSP2_x64.exe" }));
        Assert.That(info.Environment["WINEPREFIX"], Is.EqualTo(runtime.PrefixPath));
        Assert.That(info.WorkingDirectory, Is.EqualTo("/Games/KSP2"));
    }

    // CrossOver always names the Windows user "crossover", plain Wine uses the macOS account name.
    [TestCase(true, "crossover")]
    [TestCase(false, "player")]
    public void GetGameDataFolder_UsesTheRuntimesWindowsUser(bool crossOver, string expectedUser)
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        if (crossOver) AddCrossOver(fileSystem);
        else AddBundledRuntime(fileSystem);

        // Act
        var folder = Build(fileSystem).GetGameDataFolder();

        // Assert
        Assert.That(folder, Does.Contain($"/drive_c/users/{expectedUser}/AppData/LocalLow/Intercept Games/Kerbal Space Program 2"));
    }

    // DXMT needs winemetal.dll inside the prefix's system32, so the bundled runtime copies it in.
    [Test]
    public async Task PrepareAsync_NewBundledPrefix_RunsWinebootAndCopiesPrefixFiles()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        var prefixFile = fileSystem.Path.Combine(BUNDLED_ROOT, "prefix-files", "system32", "winemetal.dll");
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(prefixFile)!);
        fileSystem.File.WriteAllText(prefixFile, "dxmt");

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<System.Diagnostics.ProcessStartInfo>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Callback<System.Diagnostics.ProcessStartInfo, Action<string>, CancellationToken>((info, _, _) =>
                fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(info.Environment["WINEPREFIX"]!, "drive_c")))
            .ReturnsAsync(0);
        var service = Build(fileSystem, runner);
        var runtime = service.Detect()!;

        // Act
        await service.PrepareAsync(runtime, _ => { }, CancellationToken.None);

        // Assert
        runner.Verify(r => r.RunAsync(
            It.Is<System.Diagnostics.ProcessStartInfo>(i => i.ArgumentList.SequenceEqual(new[] { "wineboot", "--init" })),
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        var copied = fileSystem.Path.Combine(runtime.PrefixPath, "drive_c", "windows", "system32", "winemetal.dll");
        Assert.That(fileSystem.File.ReadAllText(copied), Is.EqualTo("dxmt"));
    }

    // Disabling mscoree stops Wine's Mono installer dialog from blocking the first run.
    [Test]
    public void CreateLaunchInfo_Bundled_AppliesTheRuntimesEnvironment()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        var service = Build(fileSystem);

        // Act
        var info = service.CreateLaunchInfo(service.Detect()!, "/Games/KSP2/KSP2_x64.exe", "/Games/KSP2", null);

        // Assert
        Assert.That(info.Environment["WINEDLLOVERRIDES"], Is.EqualTo("mscoree,mshtml="));
    }

    // Wine built from CrossOver's sources names the Windows user "crossover" whatever the Mac account is.
    [Test]
    public void GetGameDataFolder_ExistingPrefix_UsesTheUserFolderInIt()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        var users = fileSystem.Path.Combine(STORAGE, "wine-prefix", "drive_c", "users");
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(users, "Public"));
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(users, "crossover"));

        // Act
        var folder = Build(fileSystem).GetGameDataFolder();

        // Assert
        Assert.That(folder, Does.Contain("/drive_c/users/crossover/AppData/LocalLow/"));
    }

    // The standalone CLI lives outside the .app and would otherwise fall back to CrossOver's prefix.
    [Test]
    public void Detect_FromTheApp_RemembersWhereTheRuntimeIs()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        var config = new LauncherConfig();

        // Act
        Build(fileSystem, launcherConfig: config).Detect();

        // Assert
        Assert.That(config.WineRuntimePath, Is.EqualTo(BUNDLED_ROOT));
    }

    [Test]
    public void Detect_FromAStandaloneCli_UsesTheRememberedRuntimeOverCrossOver()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        const string elsewhere = "/Users/player/Downloads/KSP2 Redux.app/Contents/Resources/wine-runtime";
        AddBundledRuntime(fileSystem, elsewhere);
        AddCrossOver(fileSystem);
        var config = new LauncherConfig { WineRuntimePath = elsewhere };

        // Act
        var runtime = Build(fileSystem, processPath: "/Users/player/.local/bin/redux-launcher-cli", launcherConfig: config).Detect();

        // Assert
        Assert.That(runtime!.Kind, Is.EqualTo(WineRuntimeKind.Bundled));
        Assert.That(runtime.RuntimeRoot, Is.EqualTo(elsewhere));
        Assert.That(runtime.PrefixPath, Is.EqualTo(fileSystem.Path.Combine(STORAGE, WineRuntimeService.PREFIX_FOLDER)));
    }

    [Test]
    public void Detect_FromAStandaloneCli_FindsTheLauncherInApplications()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        AddCrossOver(fileSystem);

        // Act
        var runtime = Build(fileSystem, processPath: "/Users/player/.local/bin/redux-launcher-cli").Detect();

        // Assert
        Assert.That(runtime!.RuntimeRoot, Is.EqualTo(BUNDLED_ROOT));
    }

    [Test]
    public void CreateStopInfo_BundledRuntime_KillsTheWineServerOfItsPrefix()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddBundledRuntime(fileSystem);
        var service = Build(fileSystem);
        var runtime = service.Detect()!;

        // Act
        var stop = service.CreateStopInfo(runtime);

        // Assert
        Assert.That(stop.FileName, Is.EqualTo(fileSystem.Path.Combine(BUNDLED_ROOT, "wine", "bin", "wineserver")));
        Assert.That(stop.ArgumentList, Is.EqualTo(new[] { "--kill" }));
        Assert.That(stop.Environment["WINEPREFIX"], Is.EqualTo(runtime.PrefixPath));
    }

    [Test]
    public void CreateStopInfo_CrossOver_EndsItsBottleThroughWineboot()
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        AddCrossOver(fileSystem);
        var service = Build(fileSystem);
        var runtime = service.Detect()!;

        // Act
        var stop = service.CreateStopInfo(runtime);

        // Assert
        Assert.That(stop.FileName, Is.EqualTo(CROSSOVER_WINE));
        Assert.That(stop.ArgumentList, Is.EqualTo(new[] { "--bottle", WineRuntimeService.CROSSOVER_BOTTLE, "--", "wineboot", "--kill" }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void IsRosettaInstalled_FollowsRosettasMarkerFile(bool installed)
    {
        // Arrange
        var fileSystem = NewMacFileSystem();
        if (installed)
        {
            fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(WineRuntimeService.ROSETTA_MARKER)!);
            fileSystem.File.WriteAllText(WineRuntimeService.ROSETTA_MARKER, "");
        }

        // Act
        var result = Build(fileSystem).IsRosettaInstalled();

        // Assert
        Assert.That(result, Is.EqualTo(installed));
    }
}
