using System.IO.Compression;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class CacheServiceTest
{
    private const string InstallDir = @"C:\Games\Ksp2";

    private static (CacheService Service, MockFileSystem FileSystem, Mock<IZipFileService> ZipFileService) MakeService()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetTempPath());
        var zipFileService = new Mock<IZipFileService>();
        return (new CacheService(fs, zipFileService.Object), fs, zipFileService);
    }

    [Test]
    public void RecursivelyCreateCache_ZipCreationFails_DeletesThePartialTempFile()
    {
        var (service, fs, zipFileService) = MakeService();
        zipFileService.Setup(z => z.NewArchive(It.IsAny<Stream>(), It.IsAny<ZipArchiveMode>(), It.IsAny<bool>()))
            .Throws(new IOException("disk full"));

        Assert.Throws<IOException>(() => service.RecursivelyCreateCache(InstallDir));

        var leftoverTempFiles = fs.Directory.EnumerateFiles(fs.Path.GetTempPath(), "uninstall-*.zip");
        Assert.That(leftoverTempFiles, Is.Empty, "A failed snapshot must not leave a partial temp file behind.");
    }

    [Test]
    public void RecursivelyRestoreCache_UninstallZipMissing_ThrowsCacheRestoreExceptionWithContext()
    {
        var (service, _, _) = MakeService();

        var ex = Assert.Throws<CacheRestoreException>(() => service.RecursivelyRestoreCache(InstallDir));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Directory, Is.EqualTo(InstallDir));
            Assert.That(ex.ExpectedFile, Is.EqualTo("uninstall.zip"));
        });
    }

    [Test]
    public void RecursivelyRestoreCache_RemovesReduxSdkFilesButKeepsStockBundles()
    {
        // The uninstall snapshot skips StreamingAssets, so files a patch adds there survive a restore
        // unless they are purged. The player build adds the linked addressables under ReduxSDK.
        var (service, fs, zipFileService) = MakeService();
        string streamingAssets = fs.Path.Combine(InstallDir, "KSP2_x64_Data", "StreamingAssets");
        string reduxSdk = fs.Path.Combine(streamingAssets, "ReduxSDK");
        string stockBundle = fs.Path.Combine(streamingAssets, "aa", "StandaloneWindows64", "stock.bundle");
        fs.Directory.CreateDirectory(fs.Path.Combine(reduxSdk, "ExternalAddressables"));
        fs.File.WriteAllText(fs.Path.Combine(reduxSdk, "linked-addressables-build.json"), "{}");
        fs.File.WriteAllText(fs.Path.Combine(reduxSdk, "ExternalAddressables", "catalog.json"), "{}");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(stockBundle)!);
        fs.File.WriteAllText(stockBundle, "stock");
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "uninstall.zip"), "zip");
        zipFileService.Setup(z => z.OpenRead(It.IsAny<string>()))
            .Returns(Mock.Of<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>(a => a.Entries == Array.Empty<Ksp2Redux.Tools.Common.Wrappers.IZipArchiveEntry>()));

        service.RecursivelyRestoreCache(InstallDir);

        Assert.Multiple(() =>
        {
            Assert.That(fs.Directory.Exists(reduxSdk), Is.False, "Linked addressables must not outlive an uninstall.");
            Assert.That(fs.File.Exists(stockBundle), Is.True, "Stock bundles in StreamingAssets must be left alone.");
        });
    }

    // Windows refuses to delete a read-only file, which stopped the restore with the folder half emptied.
    [Test]
    public void RecursivelyRestoreCache_ReadOnlyFile_IsClearedAndRestored()
    {
        var (service, fs, zipFileService) = MakeService();
        string readOnlyFile = fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Managed", "Assembly-CSharp.dll");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(readOnlyFile)!);
        fs.File.WriteAllText(readOnlyFile, "redux");
        fs.File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "uninstall.zip"), "zip");
        zipFileService.Setup(z => z.OpenRead(It.IsAny<string>()))
            .Returns(Mock.Of<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>(a => a.Entries == Array.Empty<Ksp2Redux.Tools.Common.Wrappers.IZipArchiveEntry>()));

        service.RecursivelyRestoreCache(InstallDir);

        Assert.That(fs.File.Exists(readOnlyFile), Is.False);
        zipFileService.Verify(z => z.ExtractToDirectory(It.IsAny<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>(), InstallDir, true), Times.Once);
    }

    [Test]
    public void RecursivelyRestoreCache_DamagedSnapshot_LeavesTheInstallUntouched()
    {
        var (service, fs, zipFileService) = MakeService();
        string exe = fs.Path.Combine(InstallDir, "KSP2_x64.exe");
        string config = fs.Path.Combine(InstallDir, "Redux", "Config", "settings.json");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(config)!);
        fs.File.WriteAllText(exe, "redux");
        fs.File.WriteAllText(config, "{}");
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "uninstall.zip"), "zip");
        var damaged = new Mock<Stream> { CallBase = true };
        damaged.SetupGet(s => s.CanRead).Returns(true);
        damaged.Setup(s => s.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidDataException("CRC mismatch"));
        var entry = new Mock<Ksp2Redux.Tools.Common.Wrappers.IZipArchiveEntry>();
        entry.Setup(e => e.Open()).Returns(damaged.Object);
        var archive = new Mock<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>();
        archive.SetupGet(a => a.Entries).Returns([entry.Object]);
        zipFileService.Setup(z => z.OpenRead(It.IsAny<string>())).Returns(archive.Object);
        zipFileService.Setup(z => z.ExtractToDirectory(archive.Object, InstallDir, true))
            .Throws(new InvalidDataException("CRC mismatch"));

        Assert.Throws<InvalidDataException>(() => service.RecursivelyRestoreCache(InstallDir, true));

        Assert.Multiple(() =>
        {
            Assert.That(fs.File.ReadAllText(exe), Is.EqualTo("redux"));
            Assert.That(fs.File.Exists(config), Is.True);
            Assert.That(fs.Directory.Exists(fs.Path.Combine(InstallDir, "UninstallTemp")), Is.False);
        });
    }

    // A restore that crashed left its UninstallTemp behind. Once the install had a Redux/Config again, moving it
    // aside hit the leftover copy and every later update failed.
    [Test]
    public void RecursivelyRestoreCache_LeftoverUninstallTemp_KeepsTheLiveConfigAndRestores()
    {
        var (service, fs, zipFileService) = MakeService();
        string liveConfig = fs.Path.Combine(InstallDir, "Redux", "Config", "settings.json");
        string leftoverConfig = fs.Path.Combine(InstallDir, "UninstallTemp", "Redux", "Config", "settings.json");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(liveConfig)!);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(leftoverConfig)!);
        fs.File.WriteAllText(liveConfig, "live");
        fs.File.WriteAllText(leftoverConfig, "leftover");
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "uninstall.zip"), "zip");
        zipFileService.Setup(z => z.OpenRead(It.IsAny<string>()))
            .Returns(Mock.Of<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>(a => a.Entries == Array.Empty<Ksp2Redux.Tools.Common.Wrappers.IZipArchiveEntry>()));

        service.RecursivelyRestoreCache(InstallDir, true);

        Assert.Multiple(() =>
        {
            Assert.That(fs.File.ReadAllText(liveConfig), Is.EqualTo("live"));
            Assert.That(fs.Directory.Exists(fs.Path.Combine(InstallDir, "UninstallTemp")), Is.False);
        });
    }

    [Test]
    public void AddFolder_ManagedShapesRuntime_IsIncludedInUninstallSnapshot()
    {
        var (service, fs, _) = MakeService();
        string managedDirectory = fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Managed");
        string shapesPath = fs.Path.Combine(managedDirectory, "ShapesRuntime.dll");
        fs.Directory.CreateDirectory(managedDirectory);
        fs.File.WriteAllText(shapesPath, "stock shapes");
        var archive = new Mock<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>();

        service.AddFolder(archive.Object, InstallDir, "");

        archive.Verify(
            candidate => candidate.CreateEntryFromFile(
                shapesPath,
                fs.Path.Combine("KSP2_x64_Data", "Managed", "ShapesRuntime.dll")),
            Times.Once);
    }
}
