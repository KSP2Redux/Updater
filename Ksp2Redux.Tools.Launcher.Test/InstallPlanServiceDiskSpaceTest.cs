using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

public class InstallPlanServiceDiskSpaceTest
{
    private const string InstallDir = @"C:\Games\Ksp2";

    private static (InstallPlanService Service, Mock<IDiskSpaceService> DiskSpace, MockFileSystem Fs) MakeService()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);

        var cacheService = new Mock<ICacheService>();
        var environmentProvider = new MockEnvironmentProvider();
        var assemblyService = new Mock<IAssemblyService>();
        var moduleDefinitionService = new Mock<IModuleDefinitionService>();
        var zipFileService = new Mock<IZipFileService>();
        var diskSpace = new Mock<IDiskSpaceService>();

        var service = new InstallPlanService(fs, cacheService.Object, environmentProvider, assemblyService.Object,
            moduleDefinitionService.Object, zipFileService.Object, diskSpace.Object);
        return (service, diskSpace, fs);
    }

    [Test]
    public void ApplyToFolder_NotEnoughFreeSpace_ThrowsBeforeTouchingAnyStep()
    {
        var (service, diskSpace, _) = MakeService();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(InstallDir)).Returns(1024); // basically nothing

        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult("unused"), "big patch", downloadSize: 10L * 1024 * 1024 * 1024); // 10 GB

        Assert.That(async () => await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None),
            Throws.InvalidOperationException);
    }

    [Test]
    public async Task ApplyToFolder_EnoughFreeSpace_DoesNotThrowFromSpaceCheck()
    {
        var (service, diskSpace, _) = MakeService();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(InstallDir)).Returns(100L * 1024 * 1024 * 1024); // 100 GB

        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, ct) => Task.FromResult(InstallDir), "small patch", downloadSize: 1024);

        // A missing/invalid patch file will fail later in Ksp2Patch.FromFile - that's fine, it means the
        // space check itself let it through.
        Exception? thrown = null;
        try { await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None); }
        catch (Exception ex) { thrown = ex; }

        Assert.That(thrown?.Message ?? "", Does.Not.Contain("disk space"));
    }

    [Test]
    public async Task ApplyToFolder_DiskSpaceUnknown_SkipsCheckAndProceeds()
    {
        var (service, diskSpace, _) = MakeService();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(InstallDir)).Returns((long?)null);

        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult("unused"), "big patch", downloadSize: 10L * 1024 * 1024 * 1024);

        // Should not throw due to disk space (unknown => skip check); it'll fail later for other reasons instead.
        Exception? thrown = null;
        try { await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None); }
        catch (Exception ex) { thrown = ex; }

        Assert.That(thrown?.Message ?? "", Does.Not.Contain("disk space"));
    }
}
