using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class Ksp2GameUninstallServiceTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";
    private const string STEAM_GAME = @"D:\SteamLibrary\steamapps\common\Kerbal Space Program 2";

    private MockFileSystem _fileSystem = null!;
    private Ksp2GameUninstallService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        _service = new Ksp2GameUninstallService(_fileSystem, Mock.Of<ILogService>(),
            new InstallationStateService(_fileSystem, new Ksp2Redux.Tools.Common.Services.ZipFileService(_fileSystem)));
    }

    private void CreateGame(string folder)
    {
        _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(folder, "KSP2_x64_Data", "Managed"));
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, "KSP2_x64.exe"), "exe");
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, "KSP2_x64_Data", "Managed", "Assembly-CSharp.dll"), "dll");
    }

    [Test]
    public void Inspect_StandaloneInstall_CanBeDeleted()
    {
        // Arrange
        CreateGame(GAME);

        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(GAME, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal, Is.EqualTo(new Ksp2GameRemoval(Ksp2GameRemovalKind.DeleteFolder, GAME)));
    }

    [Test]
    public void Inspect_ExeWithoutTheGameDataBesideIt_IsNotDeletable()
    {
        // Arrange
        _fileSystem.Directory.CreateDirectory(@"C:\Users\Eivind\Downloads");
        _fileSystem.File.WriteAllText(@"C:\Users\Eivind\Downloads\KSP2_x64.exe", "exe");
        _fileSystem.File.WriteAllText(@"C:\Users\Eivind\Downloads\taxes.pdf", "important");

        // Act
        var removal = _service.Inspect(@"C:\Users\Eivind\Downloads\KSP2_x64.exe");

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.NotAGameFolder));
    }

    // A portable copy unpacked loose into a shared folder passes the exe and data checks, and deleting the folder
    // would take everything else in it.
    [Test]
    public void Inspect_GameUnpackedBesideAnotherProgram_IsNotDeletable()
    {
        // Arrange
        const string downloads = @"C:\Users\Eivind\Downloads";
        CreateGame(downloads);
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(downloads, "SteamSetup.exe"), "installer");

        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(downloads, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.NotAGameFolder));
    }

    [Test]
    public void Inspect_GameUnpackedAmongManyUnrelatedFiles_IsNotDeletable()
    {
        // Arrange
        const string downloads = @"C:\Users\Eivind\Downloads";
        CreateGame(downloads);
        for (var i = 0; i < 11; i++)
        {
            _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(downloads, $"document{i}.pdf"), "important");
        }

        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(downloads, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.NotAGameFolder));
    }

    [Test]
    public void Inspect_GameAtTheRootOfADrive_IsNotDeletable()
    {
        // Arrange
        CreateGame(@"D:\");

        // Act
        var removal = _service.Inspect(@"D:\KSP2_x64.exe");

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.NotAGameFolder));
    }

    [Test]
    public void Inspect_ModdedReduxInstall_CanBeDeleted()
    {
        // Arrange
        CreateGame(GAME);
        foreach (var folder in new[] { "MonoBleedingEdge", "PDLauncher", "Redux", "BepInEx", "mods", ".egstore" })
        {
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(GAME, folder));
        }
        foreach (var file in new[]
                 {
                     "UnityPlayer.dll", "UnityCrashHandler64.exe", "prepatched.nodelete", "redux.log",
                     "redux.log.old", "Ksp2.log", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt",
                     "ReShade.ini", "dxgi.dll", "notes.txt"
                 })
        {
            _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(GAME, file), "x");
        }
        // An unreadable backup protects the folder, so the legacy backup is a valid empty archive.
        byte[] emptyZip = [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        _fileSystem.File.WriteAllBytes(_fileSystem.Path.Combine(GAME, "uninstall.zip"), emptyZip);

        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(GAME, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.DeleteFolder));
    }

    [Test]
    public void Inspect_FolderGone_IsMissing()
    {
        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(GAME, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.Missing));
    }

    // Deleting a Steam library copy behind Steam's back leaves Steam believing it is still installed.
    [Test]
    public void Inspect_SteamLibraryInstall_GoesThroughSteam()
    {
        // Arrange
        CreateGame(STEAM_GAME);
        _fileSystem.File.WriteAllText(@"D:\SteamLibrary\steamapps\appmanifest_954850.acf", "manifest");

        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(STEAM_GAME, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.UninstallThroughSteam));
    }

    [Test]
    public void Inspect_InSteamLibraryWithoutSteamManifest_CanBeDeleted()
    {
        // Arrange
        CreateGame(STEAM_GAME);

        // Act
        var removal = _service.Inspect(_fileSystem.Path.Combine(STEAM_GAME, "KSP2_x64.exe"));

        // Assert
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.DeleteFolder));
    }

    [Test]
    public async Task DeleteAsync_DeletesTheWholeInstallAndNothingBeside()
    {
        // Arrange
        CreateGame(GAME);
        _fileSystem.Directory.CreateDirectory(@"C:\Games\Other Game");
        var readOnlyFile = _fileSystem.Path.Combine(GAME, "readonly.txt");
        _fileSystem.File.WriteAllText(readOnlyFile, "x");
        _fileSystem.File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        // Act
        await _service.DeleteAsync(_service.Inspect(_fileSystem.Path.Combine(GAME, "KSP2_x64.exe")));

        // Assert
        Assert.That(_fileSystem.Directory.Exists(GAME), Is.False);
        Assert.That(_fileSystem.Directory.Exists(@"C:\Games\Other Game"), Is.True);
    }

    [Test]
    public async Task DeleteAsync_NotAGameFolder_Throws()
    {
        // Arrange
        _fileSystem.Directory.CreateDirectory(@"C:\Users\Eivind\Downloads");

        // Act
        var removal = new Ksp2GameRemoval(Ksp2GameRemovalKind.NotAGameFolder, @"C:\Users\Eivind\Downloads");

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(removal));
        Assert.That(_fileSystem.Directory.Exists(@"C:\Users\Eivind\Downloads"), Is.True);
    }
}
