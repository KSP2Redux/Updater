using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class InstallPlanServiceRollbackTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string PatchPath = @"C:\downloads\corrupt.patch";

    private static (InstallPlanService Service, Mock<ICacheService> CacheService, MockFileSystem Fs)
        MakeService(Mock<IZipFileService> zipFileService)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(PatchPath)!);
        fs.File.WriteAllBytes(PatchPath, [0x01]);

        var cacheService = new Mock<ICacheService>();
        var environmentProvider = new MockEnvironmentProvider();
        var assemblyService = new Mock<IAssemblyService>();
        var moduleDefinitionService = new Mock<IModuleDefinitionService>();
        var diskSpace = new Mock<IDiskSpaceService>();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);

        var service = new InstallPlanService(fs, cacheService.Object, environmentProvider, assemblyService.Object,
            moduleDefinitionService.Object, zipFileService.Object, diskSpace.Object);
        return (service, cacheService, fs);
    }

    private static InstallPlan MakeCorruptPatchPlan()
    {
        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), "corrupt patch");
        return plan;
    }

    [Test]
    public async Task ApplyToFolder_PatchApplyFailsWithSnapshotAvailable_RollsBackAndThrowsInstallFailedException()
    {
        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(PatchPath)).Throws(new IOException("archive is corrupt"));

        var (service, cacheService, fs) = MakeService(zipFileService);
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, "uninstall.zip"), [0x00]); // a snapshot exists

        var ex = Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(MakeCorruptPatchPlan(), InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(ex!.RolledBack, Is.True);
        Assert.That(ex.Message, Does.Contain("rolled back"));
        Assert.That(ex.InnerException, Is.InstanceOf<IOException>());
        cacheService.Verify(c => c.RecursivelyRestoreCache(InstallDir, true), Times.Once);
    }

    [Test]
    public async Task ApplyToFolder_PatchApplyFailsAndRollbackAlsoFails_ThrowsWithBothFailuresDescribed()
    {
        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(PatchPath)).Throws(new IOException("archive is corrupt"));

        var (service, cacheService, fs) = MakeService(zipFileService);
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, "uninstall.zip"), [0x00]);
        cacheService.Setup(c => c.RecursivelyRestoreCache(InstallDir, true))
            .Throws(new IOException("disk full while restoring"));

        var ex = Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(MakeCorruptPatchPlan(), InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(ex!.RolledBack, Is.False);
        Assert.That(ex.Message, Does.Contain("archive is corrupt"));
        Assert.That(ex.Message, Does.Contain("disk full while restoring"));
    }

    [Test]
    public async Task ApplyToFolder_PatchApplyFailsWithNoSnapshot_RethrowsOriginalExceptionUnwrapped()
    {
        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(PatchPath)).Throws(new IOException("archive is corrupt"));

        var (service, cacheService, _) = MakeService(zipFileService);
        // No uninstall.zip created - nothing to roll back to yet.

        var ex = Assert.ThrowsAsync<IOException>(async () =>
            await service.ApplyToFolder(MakeCorruptPatchPlan(), InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(ex!.Message, Is.EqualTo("archive is corrupt"));
        cacheService.Verify(c => c.RecursivelyRestoreCache(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
