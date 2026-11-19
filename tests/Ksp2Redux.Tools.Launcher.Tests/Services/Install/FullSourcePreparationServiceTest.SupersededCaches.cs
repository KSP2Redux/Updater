using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Caches that an earlier build keyed by a contract listing boot.config move into place when they hold the current
/// contract's bytes, and go through a trash folder once the current cache verified.
/// </summary>
public class FullSourcePreparationServiceSupersededCachesTest
{
    private const string STORAGE = @"C:\launcher";
    private const string CACHE = STORAGE + @"\full-source-preparation-v1";
    private static readonly byte[] BOOT_BYTES = "wait-for-native-debugger=0\r\n"u8.ToArray();

    private readonly StockBaselineManifest _contract = new("contract", "pinned", [
        new StockBaselineFile("KSP2_x64.exe", 1, new string('A', 64)),
        new StockBaselineFile("KSP2_x64_Data/StreamingAssets/a.bin", 2, new string('B', 64))]);

    private MockFileSystem _fs = null!;
    private FullSourcePreparationService _service = null!;

    /// <summary>
    /// Create a service over an empty launcher storage.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        _fs.Directory.CreateDirectory(STORAGE);
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(new LauncherConfig());
        configuration.Setup(service => service.GetLocalStorageDirectory()).Returns(STORAGE);
        var os = new Mock<IOperatingSystemService>();
        os.Setup(service => service.IsWindows()).Returns(true);
        _service = new FullSourcePreparationService(_fs, configuration.Object, Mock.Of<IManifestReleasesFeedProviderService>(),
            Mock.Of<IPatchDownloadService>(), () => Mock.Of<ISteamDepotDownloader>(), new ZipFileService(_fs),
            Mock.Of<IDiskSpaceService>(), os.Object, _ => { });
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private string Tree(string name, byte[] bootConfig)
    {
        string root = _fs.Path.Combine(CACHE, "baselines", name);
        _fs.Directory.CreateDirectory(_fs.Path.Combine(root, "KSP2_x64_Data"));
        _fs.File.WriteAllBytes(_fs.Path.Combine(root, "KSP2_x64_Data", "boot.config"), bootConfig);
        _fs.File.WriteAllText(_fs.Path.Combine(root, "KSP2_x64.exe"), "x");
        return root;
    }

    private string OlderName(StockBaselineManifest contract, byte[] bootConfig) =>
        Digest(contract.ToJsonWithBootConfig(bootConfig.Length, Digest(bootConfig)));

    /// <summary>
    /// A tree named by the older form of the contract is removed, and nothing else in the cache is.
    /// </summary>
    [Test]
    public void BaselineTreeOfTheOlderContractForm_IsRemovedAndOthersStay()
    {
        string current = Tree(Digest(_contract.ToJson()), BOOT_BYTES);
        string older = Tree(OlderName(_contract, BOOT_BYTES), BOOT_BYTES);
        string otherContract = Tree(OlderName(new StockBaselineManifest("other", "pinned", [
            new StockBaselineFile("KSP2_x64.exe", 1, new string('A', 64))]), BOOT_BYTES), BOOT_BYTES);
        string otherBoot = Tree(OlderName(_contract, "different=1\r\n"u8.ToArray()), BOOT_BYTES);
        string foreign = _fs.Path.Combine(CACHE, "baselines", "not-a-digest");
        _fs.Directory.CreateDirectory(foreign);
        string outside = _fs.Path.Combine(STORAGE, "outside.txt");
        _fs.File.WriteAllText(outside, "kept");

        _service.PruneSupersededBaselines(_contract);

        Assert.That(_fs.Directory.Exists(older), Is.False);
        Assert.That(_fs.Directory.Exists(current), Is.True);
        Assert.That(_fs.Directory.Exists(otherContract), Is.True, "A tree of another contract stays.");
        Assert.That(_fs.Directory.Exists(otherBoot), Is.True, "A name that does not match the tree's boot.config stays.");
        Assert.That(_fs.Directory.Exists(foreign), Is.True);
        Assert.That(_fs.File.Exists(outside), Is.True);
        Assert.That(_fs.Directory.Exists(_fs.Path.Combine(CACHE, "trash")) &&
            _fs.Directory.EnumerateFileSystemEntries(_fs.Path.Combine(CACHE, "trash")).Any(), Is.False, "The trash is emptied.");
    }

    private string Backup(string name, StockBaselineManifest contract, byte[]? legacyManifest)
    {
        string path = _fs.Path.Combine(CACHE, "prepatch-backups", name + ".zip");
        _fs.Directory.CreateDirectory(_fs.Path.GetDirectoryName(path)!);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (Stream output = zip.CreateEntry(StockBaselineManifest.MANIFEST_PATH).Open())
            output.Write(legacyManifest ?? contract.ToJson());
        _fs.File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    /// <summary>
    /// A backup whose embedded manifest is the older form of the verified contract and which its name digests is removed.
    /// </summary>
    [Test]
    public void BackupOfTheOlderContractForm_IsRemovedOnlyWhenItsNameAndManifestAgree()
    {
        byte[] older = _contract.ToJsonWithBootConfig(BOOT_BYTES.Length, Digest(BOOT_BYTES));
        string verified = Backup(Digest(_contract.ToJson()).ToLowerInvariant(), _contract, null);
        string superseded = Backup(Digest(older).ToLowerInvariant(), _contract, older);
        string misnamed = Backup(new string('a', 64), _contract, older);
        var another = new StockBaselineManifest("another", "pinned", [new StockBaselineFile("KSP2_x64.exe", 1, new string('A', 64))]);
        byte[] anotherOlder = another.ToJsonWithBootConfig(1, new string('D', 64));
        string otherContract = Backup(Digest(anotherOlder).ToLowerInvariant(), another, anotherOlder);
        string unreadable = _fs.Path.Combine(CACHE, "prepatch-backups", "broken.zip");
        _fs.File.WriteAllText(unreadable, "not a zip");

        _service.PruneSupersededBackups(_contract, verified);

        Assert.That(_fs.File.Exists(superseded), Is.False);
        Assert.That(_fs.File.Exists(verified), Is.True);
        Assert.That(_fs.File.Exists(misnamed), Is.True);
        Assert.That(_fs.File.Exists(otherContract), Is.True);
        Assert.That(_fs.File.Exists(unreadable), Is.True);
    }

    // A contract whose files exist with their true hashes, and the cache tree of an earlier build that holds them.
    private (StockBaselineManifest Contract, string Twin) CompleteTwin(bool corrupt = false)
    {
        var contents = new Dictionary<string, byte[]>
        {
            ["KSP2_x64.exe"] = "executable"u8.ToArray(), ["KSP2_x64_Data/StreamingAssets/a.bin"] = "streaming asset"u8.ToArray()
        };
        var contract = new StockBaselineManifest("complete", "pinned",
            contents.Select(file => new StockBaselineFile(file.Key, file.Value.Length, Digest(file.Value))), ["empty"]);
        string twin = Tree(OlderName(contract, BOOT_BYTES), BOOT_BYTES);
        foreach (var file in contents)
        {
            string path = _fs.Path.Combine(twin, file.Key.Replace('/', '\\'));
            _fs.Directory.CreateDirectory(_fs.Path.GetDirectoryName(path)!);
            _fs.File.WriteAllBytes(path, corrupt && file.Key.EndsWith("a.bin") ? "corrupted asset!"u8.ToArray() : file.Value);
        }
        _fs.Directory.CreateDirectory(_fs.Path.Combine(twin, "empty"));
        return (contract, twin);
    }

    private FullSourcePreparationService ServiceWithLittleSpace()
    {
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(new LauncherConfig());
        configuration.Setup(service => service.GetLocalStorageDirectory()).Returns(STORAGE);
        var os = new Mock<IOperatingSystemService>();
        os.Setup(service => service.IsWindows()).Returns(true);
        var space = new Mock<IDiskSpaceService>();
        space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns(1024);
        return new FullSourcePreparationService(_fs, configuration.Object, Mock.Of<IManifestReleasesFeedProviderService>(),
            Mock.Of<IPatchDownloadService>(), () => Mock.Of<ISteamDepotDownloader>(), new ZipFileService(_fs),
            space.Object, os.Object, _ => { });
    }

    /// <summary>
    /// With almost no free space, a tree of the older contract form that holds the current contract's bytes is renamed
    /// into place and used, so nothing is copied.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LowSpace_BaselineTreeOfTheOlderContractForm_IsReusedByRename()
    {
        var (contract, twin) = CompleteTwin();
        var definition = new FullSourceBaselineDefinition(contract.ToJson(), []);
        string current = _fs.Path.Combine(CACHE, "baselines", Digest(contract.ToJson()));

        var source = await ServiceWithLittleSpace().SelectBaselineAsync([definition], CancellationToken.None);

        Assert.That(source.Root, Is.EqualTo(current));
        Assert.That(_fs.Directory.Exists(twin), Is.False);
        Assert.That(_fs.File.ReadAllText(_fs.Path.Combine(current, "KSP2_x64_Data", "StreamingAssets", "a.bin")), Is.EqualTo("streaming asset"));
        Assert.That(_fs.File.Exists(_fs.Path.Combine(current, "KSP2_x64_Data", "boot.config")), Is.False, "The file is in no contract.");
    }

    /// <summary>
    /// A tree of the older contract form whose bytes differ from the contract is neither used nor deleted, and the
    /// lack of space stays an error.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LowSpace_BaselineTreeThatDiffersFromTheContract_IsLeftAloneAndTheShortageStaysAnError()
    {
        var (contract, twin) = CompleteTwin(corrupt: true);
        var definition = new FullSourceBaselineDefinition(contract.ToJson(), []);

        await Assert.ThrowsAsync<IOException>(async () => await ServiceWithLittleSpace().SelectBaselineAsync([definition], CancellationToken.None));

        Assert.That(_fs.Directory.Exists(twin), Is.True);
        Assert.That(_fs.File.ReadAllText(_fs.Path.Combine(twin, "KSP2_x64_Data", "StreamingAssets", "a.bin")), Is.EqualTo("corrupted asset!"));
    }

    // The archive an earlier build kept: the current archive plus a boot.config entry and the contract's older form.
    private async Task<(StockBaselineManifest Contract, string Twin, string Current)> LegacyBackup()
    {
        string source = _fs.Path.Combine(STORAGE, "source");
        _fs.Directory.CreateDirectory(_fs.Path.Combine(source, "KSP2_x64_Data"));
        _fs.File.WriteAllText(_fs.Path.Combine(source, "KSP2_x64.exe"), "executable");
        var contract = new StockBaselineManifest("backup", "pinned", [
            new StockBaselineFile("KSP2_x64.exe", 10, Digest("executable"u8.ToArray()))], ["KSP2_x64_Data"]);
        string backups = _fs.Path.Combine(CACHE, "prepatch-backups");
        _fs.Directory.CreateDirectory(backups);
        string current = _fs.Path.Combine(backups, Digest(contract.ToJson()).ToLowerInvariant() + ".zip");
        string staged = _fs.Path.Combine(backups, "staged.zip");
        await new StockArchive(_fs, new ZipFileService(_fs)).CreateAsync(source, staged, contract);
        byte[] older = contract.ToJsonWithBootConfig(BOOT_BYTES.Length, Digest(BOOT_BYTES));
        using var stream = new MemoryStream();
        stream.Write(_fs.File.ReadAllBytes(staged));
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            zip.GetEntry(StockBaselineManifest.MANIFEST_PATH)!.Delete();
            using (Stream output = zip.CreateEntry(StockBaselineManifest.MANIFEST_PATH, CompressionLevel.NoCompression).Open())
                output.Write(older);
            using (Stream output = zip.CreateEntry("KSP2_x64_Data/boot.config", CompressionLevel.NoCompression).Open())
                output.Write(BOOT_BYTES);
        }
        string twin = _fs.Path.Combine(backups, Digest(older).ToLowerInvariant() + ".zip");
        _fs.File.WriteAllBytes(twin, stream.ToArray());
        _fs.File.Delete(staged);
        return (contract, twin, current);
    }

    /// <summary>
    /// An archive of the older contract form that verifies against the current contract is renamed to the current name.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupOfTheOlderContractForm_IsRenamedIntoPlaceWhenItVerifies()
    {
        var (contract, twin, current) = await LegacyBackup();
        byte[] bytes = _fs.File.ReadAllBytes(twin);

        Assert.That(await _service.AdoptSupersededBackupAsync(contract, current, CancellationToken.None), Is.True);

        Assert.That(_fs.File.Exists(twin), Is.False);
        Assert.That(_fs.File.ReadAllBytes(current), Is.EqualTo(bytes), "The bytes are the earlier archive's, renamed.");
        await new StockArchive(_fs, new ZipFileService(_fs)).VerifyAsync(current, contract);
    }

    /// <summary>
    /// An archive that does not verify against the current contract is not adopted and not deleted.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task BackupThatDoesNotVerify_IsNeitherAdoptedNorDeleted()
    {
        var (contract, twin, _) = await LegacyBackup();
        // The same older manifest, but the archive's exe differs from what the contract pins.
        byte[] bytes = _fs.File.ReadAllBytes(twin);
        using (var stream = new MemoryStream())
        {
            stream.Write(bytes);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
            {
                zip.GetEntry("KSP2_x64.exe")!.Delete();
                using Stream output = zip.CreateEntry("KSP2_x64.exe", CompressionLevel.NoCompression).Open();
                output.Write("tampered!!"u8);
            }
            _fs.File.WriteAllBytes(twin, stream.ToArray());
        }
        string current = _fs.Path.Combine(CACHE, "prepatch-backups", Digest(contract.ToJson()).ToLowerInvariant() + ".zip");

        Assert.That(await _service.AdoptSupersededBackupAsync(contract, current, CancellationToken.None), Is.False);

        Assert.That(_fs.File.Exists(twin), Is.True);
        Assert.That(_fs.File.Exists(current), Is.False);
    }

    /// <summary>
    /// A delete that fails part way leaves only trash, and the next preparation finishes it. Nothing outside the
    /// cache's own folders is touched.
    /// </summary>
    [Test]
    public void InterruptedDelete_LeavesOnlyTrashAndTheNextRunFinishesIt()
    {
        var (contract, twin) = CompleteTwin();
        _fs.Directory.CreateDirectory(_fs.Path.Combine(CACHE, "baselines", Digest(contract.ToJson())));
        string outside = _fs.Path.Combine(STORAGE, "outside.txt");
        _fs.File.WriteAllText(outside, "kept");
        string trash = _fs.Path.Combine(CACHE, "trash");

        using (_fs.Intercept.Deleting(FileSystemTypes.File, _ => throw new IOException("A scanner holds the file."), "*"))
            Assert.DoesNotThrow(() => _service.PruneSupersededBaselines(contract));

        Assert.That(_fs.Directory.Exists(twin), Is.False, "The rename into the trash folder is atomic.");
        Assert.That(_fs.Directory.EnumerateDirectories(trash).Count(), Is.EqualTo(1));
        Assert.That(_fs.Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories).Any(), Is.True);

        _service.SweepTrash();

        Assert.That(_fs.Directory.EnumerateFileSystemEntries(trash), Is.Empty);
        Assert.That(_fs.File.ReadAllText(outside), Is.EqualTo("kept"));
    }

    /// <summary>
    /// A preparation finishes the deletions an earlier run left, only in the trash folder.
    /// </summary>
    [Test]
    public void SweepTrash_FinishesLeftoversAndTouchesNothingElse()
    {
        string trash = _fs.Path.Combine(CACHE, "trash");
        _fs.Directory.CreateDirectory(_fs.Path.Combine(trash, "half", "KSP2_x64_Data"));
        _fs.File.WriteAllText(_fs.Path.Combine(trash, "half", "KSP2_x64_Data", "left.bin"), "x");
        _fs.File.WriteAllText(_fs.Path.Combine(trash, "old-backup.zip"), "x");
        string neighbour = Tree(new string('a', 64), BOOT_BYTES);
        string backup = _fs.Path.Combine(CACHE, "prepatch-backups", "keep.zip");
        _fs.Directory.CreateDirectory(_fs.Path.GetDirectoryName(backup)!);
        _fs.File.WriteAllText(backup, "kept");

        _service.SweepTrash();

        Assert.That(_fs.Directory.EnumerateFileSystemEntries(trash), Is.Empty);
        Assert.That(_fs.Directory.Exists(neighbour), Is.True);
        Assert.That(_fs.File.Exists(backup), Is.True);
    }

    /// <summary>
    /// An old backup is opened under the same size budget as a new one: a manifest beyond it is an error, and the
    /// archive is left where it is.
    /// </summary>
    [Test]
    public void BackupWithAnOversizedManifest_IsRefusedUnderTheArchiveBudgetAndLeftAlone()
    {
        string path = _fs.Path.Combine(CACHE, "prepatch-backups", new string('b', 64) + ".zip");
        _fs.Directory.CreateDirectory(_fs.Path.GetDirectoryName(path)!);
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (Stream output = zip.CreateEntry(StockBaselineManifest.MANIFEST_PATH, CompressionLevel.NoCompression).Open())
                output.Write(new byte[17 * 1024 * 1024]);
            _fs.File.WriteAllBytes(path, stream.ToArray());
        }

        Assert.Throws<InvalidDataException>(() => new StockArchive(_fs, new ZipFileService(_fs)).ReadEmbeddedManifest(path));
        Assert.DoesNotThrow(() => _service.PruneSupersededBackups(_contract, _fs.Path.Combine(CACHE, "prepatch-backups", "current.zip")));

        Assert.That(_fs.File.Exists(path), Is.True);
    }
}
