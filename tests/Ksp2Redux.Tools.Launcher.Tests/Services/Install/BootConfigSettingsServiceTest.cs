using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// The boot.config settings step works by key, records originals on the installation and restores them on uninstall.
/// </summary>
public class BootConfigSettingsServiceTest
{
    private const string ROOT = @"C:\Game";
    private const string BOOT = ROOT + @"\KSP2_x64_Data\boot.config";
    private MockFileSystem _fs = null!;
    private Mock<ILauncherConfigService> _configuration = null!;
    private Mock<ILogService> _log = null!;
    private Ksp2InstallEntry _entry = null!;
    private BootConfigSettingsService _service = null!;

    /// <summary>
    /// Create an installation with a stock-like boot.config.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        _fs.Directory.CreateDirectory(ROOT + @"\KSP2_x64_Data");
        _entry = new Ksp2InstallEntry { ExePath = ROOT + @"\KSP2_x64.exe" };
        _configuration = new Mock<ILauncherConfigService>();
        _configuration.Setup(service => service.TrySave()).Returns(true);
        _log = new Mock<ILogService>();
        _service = new BootConfigSettingsService(_configuration.Object, _fs, _log.Object);
    }

    private void Write(string text) => _fs.File.WriteAllBytes(BOOT, Encoding.UTF8.GetBytes(text));

    private string Read() => Encoding.UTF8.GetString(_fs.File.ReadAllBytes(BOOT));

    private void Install(Dictionary<string, string>? release)
    {
        _service.SetReleaseSettings(_entry, release);
        _service.Apply(_entry);
    }

    /// <summary>
    /// The launcher's own setting wins over the same key in the release.
    /// </summary>
    [TestCase(false, "1")]
    [TestCase(true, "0")]
    public void LauncherSetting_OverridesTheReleaseSetting(bool disableGraphicsJobs, string expected)
    {
        Write("wait-for-native-debugger=1\r\n");
        _entry.DisableGraphicsJobs = disableGraphicsJobs;

        Install(new() { ["gfx-enable-gfx-jobs"] = disableGraphicsJobs ? "1" : "0", ["wait-for-native-debugger"] = "0" });

        Assert.That(Read(), Is.EqualTo($"wait-for-native-debugger=0\r\ngfx-enable-gfx-jobs={expected}\r\n"));
    }

    /// <summary>
    /// Install, an update that drops keys and uninstall bring every managed key to its stock state, and the
    /// build identity and the player's own lines are never touched.
    /// </summary>
    [Test]
    public void InstallUpdateUninstall_RestoresTheStockStateOfEveryManagedKey()
    {
        // A stock file, plus the line an earlier launcher build wrote and a line of the player's own.
        Write("wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\ngc-max-time-slice=3\r\nbuild-guid=abc\r\n" +
            "gfx-enable-gfx-jobs=1\r\nkeep=me\r\n");

        Install(new() { ["hdr-display-enabled"] = "1", ["gc-max-time-slice"] = "3", ["gfx-threading-mode"] = "6", ["enable-directstorage"] = "True" });
        Assert.That(Read(), Is.EqualTo("wait-for-native-debugger=0\r\nhdr-display-enabled=1\r\ngc-max-time-slice=3\r\nbuild-guid=abc\r\n" +
            "gfx-enable-gfx-jobs=1\r\nkeep=me\r\ngfx-threading-mode=6\r\nenable-directstorage=True\r\n"));
        Assert.That(_entry.BootConfigOriginals, Is.EqualTo(new Dictionary<string, string?>
        {
            ["hdr-display-enabled"] = "0", ["gc-max-time-slice"] = "3", ["gfx-threading-mode"] = null,
            ["enable-directstorage"] = null, ["gfx-enable-gfx-jobs"] = null
        }), "The stock state is the original, not what the file held.");

        // The next release sets only hdr-display-enabled.
        Install(new() { ["hdr-display-enabled"] = "1" });
        Assert.That(Read(), Is.EqualTo("wait-for-native-debugger=0\r\nhdr-display-enabled=1\r\ngc-max-time-slice=3\r\nbuild-guid=abc\r\n" +
            "gfx-enable-gfx-jobs=1\r\nkeep=me\r\n"));
        Assert.That(_entry.BootConfigOriginals.Keys, Is.EquivalentTo(new[] { "hdr-display-enabled", "gfx-enable-gfx-jobs" }));

        _service.Revert(_entry);
        Assert.That(Read(), Is.EqualTo("wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\ngc-max-time-slice=3\r\nbuild-guid=abc\r\n" +
            "keep=me\r\n"));
        Assert.That(_entry.BootConfigOriginals, Is.Empty);
        Assert.That(_entry.BootConfigReleaseSettings, Is.Null);
    }

    /// <summary>
    /// A key that already holds its desired value is recorded too, so uninstall still removes it.
    /// </summary>
    [Test]
    public void KeyAlreadyHoldingItsValue_IsRecordedAndRevertsToStock()
    {
        Write("enable-directstorage=True\nhdr-display-enabled=1\n");

        Install(new() { ["enable-directstorage"] = "True", ["hdr-display-enabled"] = "1" });

        Assert.That(_entry.BootConfigOriginals, Is.EqualTo(new Dictionary<string, string?>
        {
            ["enable-directstorage"] = null, ["hdr-display-enabled"] = "0", ["gfx-enable-gfx-jobs"] = null
        }));
        _service.Revert(_entry);
        Assert.That(Read(), Is.EqualTo("hdr-display-enabled=0\n"));
    }

    /// <summary>
    /// The build identity is never written, never changed and never reverted, even if a record names it.
    /// </summary>
    [Test]
    public void BuildGuid_IsNeverManaged()
    {
        Write("build-guid=abc\n");
        _entry.BootConfigOriginals = new() { ["build-guid"] = "old" };

        Install(new() { ["build-guid"] = "other" });

        Assert.That(Read(), Is.EqualTo("build-guid=abc\ngfx-enable-gfx-jobs=1\n"));
        Assert.That(_entry.BootConfigOriginals.ContainsKey("build-guid"), Is.False);
        _entry.BootConfigOriginals["build-guid"] = "old";

        _service.Revert(_entry);

        Assert.That(Read(), Is.EqualTo("build-guid=abc\n"));
    }

    /// <summary>
    /// A record that cannot be saved stops the write, and the next apply saves the record and writes.
    /// </summary>
    [Test]
    public void RecordThatCannotBeSaved_StopsTheWriteForNow()
    {
        Write("keep=me\n");
        _configuration.Setup(service => service.TrySave()).Returns(false);

        Install(new() { ["k"] = "v" });

        Assert.That(Read(), Is.EqualTo("keep=me\n"));
        Assert.That(_entry.BootConfigOriginals, Is.Empty, "A record that is not durable is not kept in memory.");
        _log.Verify(log => log.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);

        _configuration.Setup(service => service.TrySave()).Returns(true);
        _service.Apply(_entry);

        Assert.That(Read(), Is.EqualTo("keep=me\nk=v\ngfx-enable-gfx-jobs=1\n"));
        Assert.That(_entry.BootConfigOriginals.Keys, Is.EquivalentTo(new[] { "k", "gfx-enable-gfx-jobs" }));
    }

    /// <summary>
    /// The record of a new key is saved while the file is still untouched.
    /// </summary>
    [Test]
    public void RecordOfANewKey_IsSavedBeforeTheFileIsWritten()
    {
        Write("keep=me\n");
        var events = new List<string>();
        _configuration.Setup(service => service.TrySave()).Returns(() =>
        {
            events.Add("save:" + string.Join(",", _entry.BootConfigOriginals.Keys.Order()) + ":" + Read().Replace("\n", "|"));
            return true;
        });

        Install(new() { ["k"] = "v" });
        Assert.That(events, Is.EqualTo(new[] { "save:gfx-enable-gfx-jobs,k:keep=me|" }), "Saved while the file was still untouched.");
    }

    /// <summary>
    /// When a release drops a key and the file cannot be written, the key stays in the record, and a later
    /// apply or the uninstall still removes it.
    /// </summary>
    /// <param name="laterRunIsUninstall">Whether the later run is the uninstall rather than another apply.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void FailedWriteWhenReleaseDropsAKey_KeepsTheKeyInTheRecord(bool laterRunIsUninstall)
    {
        Write("a=1\n");
        Install(new() { ["k"] = "v" });
        Assert.That(Read(), Is.EqualTo("a=1\nk=v\ngfx-enable-gfx-jobs=1\n"));

        _service.SetReleaseSettings(_entry, null);
        using (_fs.Intercept.Creating(FileSystemTypes.File, _ => throw new IOException("The folder is full."), "*.tmp"))
            _service.Apply(_entry);

        Assert.That(Read(), Is.EqualTo("a=1\nk=v\ngfx-enable-gfx-jobs=1\n"));
        Assert.That(_entry.BootConfigOriginals.Keys, Does.Contain("k"));
        _log.Verify(log => log.Warn(It.Is<string>(text => text.Contains("Could not write")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        if (laterRunIsUninstall)
        {
            _service.Revert(_entry);
            Assert.That(Read(), Is.EqualTo("a=1\n"));
            Assert.That(_entry.BootConfigOriginals, Is.Empty);
        }
        else
        {
            _service.Apply(_entry);
            Assert.That(Read(), Is.EqualTo("a=1\ngfx-enable-gfx-jobs=1\n"));
            Assert.That(_entry.BootConfigOriginals.Keys, Is.EquivalentTo(new[] { "gfx-enable-gfx-jobs" }));
        }
    }

    /// <summary>
    /// Changing the graphics jobs option updates the line, and uninstall still removes a line the player did not have.
    /// </summary>
    [Test]
    public void ToggleChange_UpdatesTheLineAndUninstallStillRemovesIt()
    {
        Write("keep=me\n");
        _service.Apply(_entry);
        Assert.That(Read(), Is.EqualTo("keep=me\ngfx-enable-gfx-jobs=1\n"));

        _entry.DisableGraphicsJobs = true;
        _service.Apply(_entry);
        Assert.That(Read(), Is.EqualTo("keep=me\ngfx-enable-gfx-jobs=0\n"));

        _service.Revert(_entry);
        Assert.That(Read(), Is.EqualTo("keep=me\n"));
    }

    /// <summary>
    /// A file that is already correct is neither written nor saved again.
    /// </summary>
    [Test]
    public void AlreadyCorrectFile_IsNotWrittenAgain()
    {
        Write("a=1\n");
        Install(new() { ["b"] = "2" });
        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        _fs.File.SetLastWriteTimeUtc(BOOT, sentinel);
        _configuration.Invocations.Clear();

        _service.Apply(_entry);

        Assert.That(_fs.File.GetLastWriteTimeUtc(BOOT), Is.EqualTo(sentinel));
        _configuration.Verify(service => service.Save(), Times.Never);
        _configuration.Verify(service => service.TrySave(), Times.Never);
    }

    /// <summary>
    /// A missing boot.config is logged and skipped, never created.
    /// </summary>
    [Test]
    public void MissingFile_IsSkippedAndNotCreated()
    {
        Install(new() { ["a"] = "1" });

        Assert.That(_fs.File.Exists(BOOT), Is.False);
        Assert.That(_entry.BootConfigOriginals, Is.Empty);
        _log.Verify(log => log.Info(It.Is<string>(text => text.Contains("no boot.config", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>
    /// Without a record, uninstall leaves the file exactly as it is.
    /// </summary>
    [Test]
    public void RevertWithoutRecord_LeavesTheFileAlone()
    {
        const string text = "gfx-enable-gfx-jobs=0\r\nhand=edited\r\n";
        Write(text);

        _service.Revert(_entry);

        Assert.That(Read(), Is.EqualTo(text));
    }

    /// <summary>
    /// A file another program holds is a warning, not a failure, and the next apply writes it.
    /// </summary>
    [Test]
    public void LockedFile_WarnsAndIsRetriedNextTime()
    {
        Write("a=1\n");
        using (_fs.File.Open(BOOT, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.DoesNotThrow(() => _service.Apply(_entry));
        }
        _log.Verify(log => log.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
        Assert.That(Read(), Is.EqualTo("a=1\n"));

        _service.Apply(_entry);

        Assert.That(Read(), Is.EqualTo("a=1\ngfx-enable-gfx-jobs=1\n"));
    }

    /// <summary>
    /// A file larger than the bound is logged and skipped, on apply and on uninstall, and the record is kept.
    /// </summary>
    [Test]
    public void FileLargerThanTheBound_IsSkippedAndKeepsTheRecord()
    {
        string large = "a=1\n" + new string('x', 64 * 1024);
        Write(large);

        Install(new() { ["k"] = "v" });

        Assert.That(Read(), Is.EqualTo(large));
        Assert.That(_entry.BootConfigOriginals, Is.Empty);
        _entry.BootConfigOriginals = new() { ["k"] = null };

        _service.Revert(_entry);

        Assert.That(Read(), Is.EqualTo(large));
        Assert.That(_entry.BootConfigOriginals.Keys, Is.EquivalentTo(new[] { "k" }));
        _log.Verify(log => log.Warn(It.Is<string>(text => text.Contains("larger than 64 KiB")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Test]
    public void ApplyThatWouldExceedTheReadLimit_LeavesTheFileAndUndoRecordAlone()
    {
        string original = "#" + new string('x', BootConfigFile.MAX_BYTES - 2) + "\n";
        Write(original);

        _service.Apply(_entry);

        Assert.That(Read(), Is.EqualTo(original));
        Assert.That(_entry.BootConfigOriginals, Is.Empty);
    }

    [Test]
    public void StockRestoreThatWouldExceedTheReadLimit_CanBeRetried()
    {
        string original = "#" + new string('x', BootConfigFile.MAX_BYTES - 2) + "\n";
        Write(original);
        var stock = new Dictionary<string, string> { ["wait-for-native-debugger"] = "0" };

        Assert.That(_service.TryRestoreStockSettings(_entry, stock), Is.False);
        Assert.That(Read(), Is.EqualTo(original));

        Write("build-guid=keep\n");
        Assert.That(_service.TryRestoreStockSettings(_entry, stock), Is.True);
        Assert.That(Read(), Is.EqualTo("build-guid=keep\nwait-for-native-debugger=0\n"));
    }

    /// <summary>
    /// The file is replaced from a temporary file in its own folder, and a write that cannot start leaves the old content.
    /// </summary>
    [Test]
    public void Write_ReplacesTheFileFromATemporaryFileInItsFolder()
    {
        Write("a=1\n");
        var created = new List<string>();

        using (_fs.Intercept.Creating(FileSystemTypes.File, change => created.Add(change.Path), "*.tmp"))
            Install(new() { ["k"] = "v" });

        Assert.That(created, Has.Count.EqualTo(1));
        Assert.That(_fs.Path.GetDirectoryName(created[0]), Is.EqualTo(ROOT + @"\KSP2_x64_Data"));
        Assert.That(_fs.File.Exists(created[0]), Is.False, "The temporary file is gone once it replaced boot.config.");
        Assert.That(_fs.Directory.GetFiles(ROOT + @"\KSP2_x64_Data"), Is.EqualTo(new[] { BOOT }));
        Assert.That(Read(), Is.EqualTo("a=1\nk=v\ngfx-enable-gfx-jobs=1\n"));
    }

    /// <summary>
    /// Temporary files of earlier writes that stopped are deleted before the next write, and nothing else is.
    /// </summary>
    [Test]
    public void Write_DeletesOnlyItsOwnLeftoverTemporaryFiles()
    {
        Write("a=1\n");
        string folder = ROOT + @"\KSP2_x64_Data";
        string leftover = folder + @"\boot.config." + new string('a', 32) + ".tmp";
        string[] others =
        [
            folder + @"\boot.config.backup.tmp", folder + @"\boot.config." + new string('g', 32) + ".tmp",
            folder + @"\other." + new string('a', 32) + ".tmp", ROOT + @"\boot.config." + new string('b', 32) + ".tmp"
        ];
        _fs.File.WriteAllText(leftover, "partial");
        foreach (string other in others)
            _fs.File.WriteAllText(other, "keep");

        Install(new() { ["k"] = "v" });

        Assert.That(_fs.File.Exists(leftover), Is.False);
        Assert.That(others.Select(_fs.File.Exists), Is.All.True);
        Assert.That(Read(), Is.EqualTo("a=1\nk=v\ngfx-enable-gfx-jobs=1\n"));
    }

    /// <summary>
    /// An installation converted by an earlier launcher has no record of the keys that launcher wrote. The keys of its
    /// original boot.config become the originals, replace a recorded storefront guess, and uninstall restores them
    /// without touching the build identity or lines the original did not have.
    /// </summary>
    [Test]
    public void RecordedStockSettings_AreRestoredByUninstall()
    {
        Write("wait-for-native-debugger=0\r\nhdr-display-enabled=1\r\ngc-max-time-slice=9\r\nbuild-guid=redux\r\nextra=2\r\nredux-only=1\r\n");
        _entry.BootConfigOriginals = new() { ["gc-max-time-slice"] = "3", ["gfx-enable-gfx-jobs"] = null };

        bool restored = _service.TryRestoreStockSettings(_entry, new Dictionary<string, string>
        {
            ["hdr-display-enabled"] = "0", ["gc-max-time-slice"] = "5", ["extra"] = "1", ["build-guid"] = "stock", ["bad key"] = "1"
        });

        Assert.That(restored, Is.True);
        _configuration.Verify(service => service.TrySave(), Times.Once);

        Assert.That(Read(), Is.EqualTo("wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\ngc-max-time-slice=5\r\nbuild-guid=redux\r\nextra=1\r\nredux-only=1\r\n"));
        Assert.That(_entry.BootConfigOriginals, Is.Empty);
    }

    /// <summary>
    /// A failed configuration save leaves the undo record available for another restore.
    /// </summary>
    [Test]
    public void RestoringStockSettings_RetriesWhenClearingTheUndoRecordFails()
    {
        _entry.BootConfigOriginals = new() { ["hdr-display-enabled"] = "0" };

        _configuration.Setup(service => service.TrySave()).Returns(false);
        Write("hdr-display-enabled=1\n");
        var archived = new Dictionary<string, string> { ["gc-max-time-slice"] = "3" };
        Assert.That(_service.TryRestoreStockSettings(_entry, archived), Is.False);

        Assert.That(_entry.BootConfigOriginals.Keys, Is.EqualTo(new[] { "hdr-display-enabled" }));
        _log.Verify(log => log.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _configuration.Setup(service => service.TrySave()).Returns(true);
        Assert.That(_service.TryRestoreStockSettings(_entry, archived), Is.True);
        Assert.That(Read(), Is.EqualTo("hdr-display-enabled=0\ngc-max-time-slice=3\n"));
        Assert.That(_entry.BootConfigOriginals, Is.Empty);
    }
}
