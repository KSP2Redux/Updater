using System.IO.Compression;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class SaveBackupServiceTest
{
    private const string GAME_DATA = @"C:\Users\Jeb\AppData\LocalLow\Intercept Games\Kerbal Space Program 2";
    private const string SAVE = GAME_DATA + @"\Saves\SinglePlayer\Default\autosave_1.json";
    private const string CRAFT = GAME_DATA + @"\Workspaces\rocket.json";

    private MockFileSystem _fileSystem = null!;
    private SaveBackupService _service = null!;
    private Mock<IGameDataFolderService> _gameDataFolder = null!;
    private Mock<IKsp2InstallService> _installs = null!;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        _gameDataFolder = new Mock<IGameDataFolderService>();
        _gameDataFolder.Setup(g => g.Resolve(It.IsAny<Ksp2InstallEntry?>())).Returns(GAME_DATA);
        _installs = new Mock<IKsp2InstallService>();
        _installs.Setup(i => i.Entries).Returns([]);
        var config = new Mock<ILauncherConfigService>();
        config.Setup(c => c.GetLocalStorageDirectory()).Returns(@"C:\Storage");
        _service = new SaveBackupService(_fileSystem, new ZipFileService(_fileSystem), _gameDataFolder.Object, _installs.Object, config.Object);
    }

    private void Write(string path, string content, int minutesAgo = 60)
    {
        _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        _fileSystem.File.WriteAllText(path, content);
        _fileSystem.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private List<string> EntriesOf(SaveBackup backup)
    {
        using var zip = new ZipFileService(_fileSystem).NewArchive(_fileSystem.File.OpenRead(backup.Path), ZipArchiveMode.Read, false);
        return zip.Entries.Select(entry => entry.FullName).ToList();
    }

    [Test]
    public void BackUp_KeepsSavesAndCraftsButNotTheGamesOwnFiles()
    {
        Write(SAVE, "save");
        Write(CRAFT, "craft");
        Write(GAME_DATA + @"\StockWorkspaces\stock.json", "stock");
        Write(GAME_DATA + @"\Player.log", "log");

        var backup = _service.BackUp();

        Assert.That(EntriesOf(backup!), Is.EquivalentTo(new[]
        {
            "Saves/SinglePlayer/Default/autosave_1.json",
            "Workspaces/rocket.json",
        }));
    }

    [Test]
    public void BackUp_NothingChangedSinceTheLastOne_IsSkipped()
    {
        Write(SAVE, "save");
        _service.BackUp();

        Assert.That(_service.BackUp(), Is.Null);

        Write(SAVE, "played some more", minutesAgo: -1);
        Assert.That(_service.BackUp(), Is.Not.Null);
    }

    [Test]
    public void BackUp_Cancelled_StopsAndLeavesNoPartialFile()
    {
        Write(SAVE, "save");

        Assert.Throws<OperationCanceledException>(() => _service.BackUp(cancellationToken: new CancellationToken(canceled: true)));
        Assert.That(_fileSystem.Directory.EnumerateFiles(@"C:\Storage", "*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void BackUp_NoSaves_WritesNothing()
    {
        Assert.That(_service.BackUp(), Is.Null);
        Assert.That(_service.List(), Is.Empty);
    }

    [Test]
    public void BackUp_KeepsOnlyTheNewestFive()
    {
        List<string> made = [];
        for (var i = 0; i < 7; i++)
        {
            Write(SAVE, $"save {i}", minutesAgo: -1 - i);
            var backup = _service.BackUp()!;
            _fileSystem.File.SetLastWriteTimeUtc(backup.Path, DateTime.UtcNow.AddDays(-1).AddMinutes(i));
            made.Add(backup.Path);
        }

        Assert.That(_service.List().Select(backup => backup.Path), Is.EqualTo(made.Skip(2).Reverse()));
    }

    [Test]
    public void Restore_PutsTheSavesBackAndBacksUpTheCurrentOnesFirst()
    {
        Write(SAVE, "before the update");
        var beforeUpdate = _service.BackUp()!;
        Write(SAVE, "after the update", minutesAgo: -1);

        _service.Restore(beforeUpdate);

        Assert.That(_fileSystem.File.ReadAllText(SAVE), Is.EqualTo("before the update"));
        var safetyCopy = _service.List().First(backup => backup.Path != beforeUpdate.Path);
        using var zip = new ZipFileService(_fileSystem).NewArchive(_fileSystem.File.OpenRead(safetyCopy.Path), ZipArchiveMode.Read, false);
        using var reader = new StreamReader(zip.GetEntry("Saves/SinglePlayer/Default/autosave_1.json")!.Open());
        Assert.That(reader.ReadToEnd(), Is.EqualTo("after the update"));
        Assert.That(_fileSystem.Directory.GetDirectories(GAME_DATA), Has.None.Contains(".redux-restore-"));
    }

    [Test]
    public void Restore_SaveCopiedInWithAnOldTimestamp_IsBackedUpBeforeItIsReplaced()
    {
        Write(SAVE, "campaign");
        var backup = _service.BackUp()!;
        Write(GAME_DATA + @"\Saves\SinglePlayer\Copied\quicksave.json", "copied from a friend", minutesAgo: 600);

        _service.Restore(backup);

        Assert.That(_service.List().Where(b => b.Path != backup.Path).SelectMany(EntriesOf),
            Has.Member("Saves/SinglePlayer/Copied/quicksave.json"));
    }

    [Test]
    public void Restore_WithFiveBackups_DeletesNone()
    {
        for (var i = 0; i < 5; i++)
        {
            Write(SAVE, $"save {i}", minutesAgo: -1 - i);
            _fileSystem.File.SetLastWriteTimeUtc(_service.BackUp()!.Path, DateTime.UtcNow.AddDays(-1).AddMinutes(i));
        }
        var oldest = _service.List().Last();

        _service.Restore(_service.List().First());

        Assert.That(_service.List(), Has.Count.EqualTo(6));
        Assert.That(_fileSystem.File.Exists(oldest.Path), Is.True);
    }

    [Test]
    public void BackUp_TwoProtonPrefixes_KeepTheirOwnBackups()
    {
        const string otherGameData = @"C:\Games\steamapps\compatdata\954850\pfx\Kerbal Space Program 2";
        var active = new Ksp2InstallEntry { ExePath = @"C:\Games\Active\KSP2_x64.exe" };
        var other = new Ksp2InstallEntry { ExePath = @"C:\Games\steamapps\common\KSP2\KSP2_x64.exe" };
        _installs.Setup(i => i.Entries).Returns([active, other]);
        _installs.Setup(i => i.ActiveEntry).Returns(active);
        _gameDataFolder.Setup(g => g.Resolve(other)).Returns(otherGameData);
        Write(SAVE, "active save");
        Write(otherGameData + @"\Saves\SinglePlayer\Default\autosave_1.json", "other save");

        var otherBackup = _service.BackUp(@"C:\Games\steamapps\common\KSP2");
        var activeBackup = _service.BackUp(@"C:\Games\Active");

        Assert.That(otherBackup, Is.Not.Null);
        Assert.That(activeBackup, Is.Not.Null, "A newer backup of another prefix must not make this one look unchanged.");
        Assert.That(_service.List().Select(backup => backup.Path), Is.EqualTo(new[] { activeBackup!.Path }));
        Assert.Throws<InvalidOperationException>(() => _service.Restore(otherBackup!));
        Assert.That(_fileSystem.File.ReadAllText(SAVE), Is.EqualTo("active save"));
    }

    [Test]
    public void Restore_LeavesFoldersTheBackupDoesNotHave()
    {
        Write(SAVE, "save");
        var savesOnly = _service.BackUp()!;
        Write(CRAFT, "craft built later", minutesAgo: -1);

        _service.Restore(savesOnly);

        Assert.That(_fileSystem.File.ReadAllText(CRAFT), Is.EqualTo("craft built later"));
    }

    [TestCase("../outside.json")]
    [TestCase("Global/settings.json")]
    public void Restore_FileOutsideTheSaveFolders_ChangesNothing(string entryName)
    {
        Write(SAVE, "current");
        var crafted = _fileSystem.Path.Combine(_service.BackupDirectory!, "saves-20260101-000000-abcdef.zip");
        _fileSystem.Directory.CreateDirectory(_service.BackupDirectory!);
        using (var stream = _fileSystem.File.Create(crafted))
        using (var zip = new ZipFileService(_fileSystem).NewArchive(stream, ZipArchiveMode.Create, false))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("Saves/SinglePlayer/Default/autosave_1.json").Open())) writer.Write("evil");
            using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open())) writer.Write("evil");
        }

        Assert.Throws<InvalidDataException>(() => _service.Restore(_service.List().Single(backup => backup.Path == crafted)));
        Assert.That(_fileSystem.File.ReadAllText(SAVE), Is.EqualTo("current"));
        Assert.That(_fileSystem.File.Exists(GAME_DATA + @"\outside.json"), Is.False);
        Assert.That(_fileSystem.Directory.Exists(GAME_DATA + @"\Global"), Is.False);
    }

    [Test]
    public void Restore_DamagedBackup_ChangesNothing()
    {
        Write(SAVE, "current");
        var damaged = _fileSystem.Path.Combine(_service.BackupDirectory!, "saves-20260101-000000-abcdef.zip");
        _fileSystem.Directory.CreateDirectory(_service.BackupDirectory!);
        _fileSystem.File.WriteAllText(damaged, "not a zip");

        Assert.Throws<InvalidDataException>(() => _service.Restore(_service.List().Single()));
        Assert.That(_fileSystem.File.ReadAllText(SAVE), Is.EqualTo("current"));
    }

    [Test]
    public void BackUpAndRestore_OnTheRealFileSystem_RoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), $"save-backup-test-{Guid.NewGuid():N}");
        var gameData = Path.Combine(root, "Kerbal Space Program 2");
        var save = Path.Combine(gameData, "Saves", "SinglePlayer", "Default", "autosave_1.json");
        var fileSystem = new RealFileSystem();
        var gameDataFolder = new Mock<IGameDataFolderService>();
        gameDataFolder.Setup(g => g.Resolve(It.IsAny<Ksp2InstallEntry?>())).Returns(gameData);
        var installs = new Mock<IKsp2InstallService>();
        installs.Setup(i => i.Entries).Returns([]);
        var config = new Mock<ILauncherConfigService>();
        config.Setup(c => c.GetLocalStorageDirectory()).Returns(Path.Combine(root, "storage"));
        var service = new SaveBackupService(fileSystem, new ZipFileService(fileSystem), gameDataFolder.Object, installs.Object, config.Object);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(save)!);
            File.WriteAllText(save, "before");
            var backup = service.BackUp()!;
            File.WriteAllText(save, "after");

            service.Restore(backup);

            Assert.That(File.ReadAllText(save), Is.EqualTo("before"));
            Assert.That(Directory.GetDirectories(gameData), Has.None.Contains(".redux-restore-"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
