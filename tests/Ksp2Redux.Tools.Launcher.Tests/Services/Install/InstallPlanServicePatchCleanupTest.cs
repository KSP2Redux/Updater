using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Wrappers;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class InstallPlanServicePatchCleanupTest
{
    private const string INSTALL_DIR = @"C:\Games\Ksp2";
    private const string PATCH_PATH = @"C:\downloads\release.patch";

    [Test]
    public async Task ApplyToFolder_DownloadedPatchAppliedSuccessfully_DisposesArchiveBeforeDeletingFile()
    {
        var innerFileSystem = new MockFileSystem(options =>
            options.SimulatingOperatingSystem(SimulationMode.Windows));
        innerFileSystem.Directory.CreateDirectory(INSTALL_DIR);
        innerFileSystem.Directory.CreateDirectory(innerFileSystem.Path.GetDirectoryName(PATCH_PATH)!);
        innerFileSystem.File.WriteAllBytes(PATCH_PATH, [0x01]);

        bool archiveDisposed = false;
        bool patchDeleted = false;
        var file = new Mock<IFile>();
        file.Setup(instance => instance.Exists(It.IsAny<string>()))
            .Returns((string path) => innerFileSystem.File.Exists(path));
        file.Setup(instance => instance.Delete(It.IsAny<string>()))
            .Callback((string path) =>
            {
                if (path == PATCH_PATH && !archiveDisposed)
                    throw new IOException("The patch archive is still open.");

                innerFileSystem.File.Delete(path);
                patchDeleted |= path == PATCH_PATH;
            });

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.SetupGet(instance => instance.File).Returns(file.Object);
        fileSystem.SetupGet(instance => instance.Directory).Returns(innerFileSystem.Directory);
        fileSystem.SetupGet(instance => instance.Path).Returns(innerFileSystem.Path);
        fileSystem.SetupGet(instance => instance.FileInfo).Returns(innerFileSystem.FileInfo);
        fileSystem.SetupGet(instance => instance.FileStream).Returns(innerFileSystem.FileStream);

        var manifestEntry = new Mock<IZipArchiveEntry>();
        manifestEntry.Setup(instance => instance.Open())
            .Returns(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"operations\":[]}")));
        var archive = new Mock<IZipArchive>();
        archive.SetupGet(instance => instance.Mode).Returns(ZipArchiveMode.Read);
        archive.Setup(instance => instance.GetEntry("manifest.json")).Returns(manifestEntry.Object);
        archive.Setup(instance => instance.Dispose()).Callback(() => archiveDisposed = true);

        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(instance => instance.OpenRead(PATCH_PATH)).Returns(archive.Object);
        var diskSpace = new Mock<IDiskSpaceService>();
        diskSpace.Setup(instance => instance.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var patchDownloadService = new Mock<IPatchDownloadService>();
        patchDownloadService.Setup(instance => instance.EnqueueAll(
                It.IsAny<IReadOnlyList<PatchDownloadRequest>>(), It.IsAny<PatchDownloadSource>(),
                It.IsAny<int>(), It.IsAny<Action<string>>(), It.IsAny<Action<long, long>>(),
                It.IsAny<CancellationToken>()))
            .Returns([]);
        var configService = new Mock<ILauncherConfigService>();
        configService.SetupGet(instance => instance.Config).Returns(new LauncherConfig("config.json"));

        var service = new InstallPlanService(
            fileSystem.Object,
            new Mock<ICacheService>().Object,
            new MockEnvironmentProvider(),
            new Mock<IAssemblyService>().Object,
            new Mock<IModuleDefinitionService>().Object,
            zipFileService.Object,
            diskSpace.Object,
            patchDownloadService.Object,
            configService.Object);
        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PATCH_PATH), "downloaded patch");

        await service.ApplyToFolder(
            plan, INSTALL_DIR, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(archiveDisposed, Is.True);
        Assert.That(patchDeleted, Is.True);
        Assert.That(innerFileSystem.File.Exists(PATCH_PATH), Is.False);
    }
}
