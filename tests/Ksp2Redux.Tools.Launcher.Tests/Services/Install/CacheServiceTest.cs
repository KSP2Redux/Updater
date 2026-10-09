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
            .Returns(new Mock<Ksp2Redux.Tools.Common.Wrappers.IZipArchive>().Object);

        service.RecursivelyRestoreCache(InstallDir);

        Assert.Multiple(() =>
        {
            Assert.That(fs.Directory.Exists(reduxSdk), Is.False, "Linked addressables must not outlive an uninstall.");
            Assert.That(fs.File.Exists(stockBundle), Is.True, "Stock bundles in StreamingAssets must be left alone.");
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
