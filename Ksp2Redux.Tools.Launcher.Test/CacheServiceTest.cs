using System.IO.Compression;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

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
}
