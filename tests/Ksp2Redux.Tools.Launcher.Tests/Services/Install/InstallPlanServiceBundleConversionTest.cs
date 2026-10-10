using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// The bundle conversion edits stock bundles in StreamingAssets, which the uninstall snapshot does not
/// cover, and keeps its journal under Redux/, which a snapshot restore deletes. So it has to be undone
/// before a restore that leaves the install stock, and kept when patching follows.
/// </summary>
public class InstallPlanServiceBundleConversionTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string PatchPath = @"C:\downloads\corrupt.patch";

    private static (InstallPlanService Service, Mock<ICacheService> CacheService, Mock<IBundleConversionService> Conversion, List<string> Calls)
        MakeService()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(PatchPath)!);
        fs.File.WriteAllBytes(PatchPath, [0x01]);
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, "uninstall.zip"), [0x00]);

        var calls = new List<string>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(c => c.RecursivelyRestoreCache(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => calls.Add("restore"));
        var conversion = new Mock<IBundleConversionService>();
        conversion.Setup(c => c.Revert(InstallDir, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("revert"));
        conversion.Setup(c => c.Convert(InstallDir, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("convert"));

        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(PatchPath)).Throws(new IOException("archive is corrupt"));
        var diskSpace = new Mock<IDiskSpaceService>();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var patchDownloadService = new Mock<IPatchDownloadService>();
        patchDownloadService.Setup(service => service.EnqueueAll(
                It.IsAny<IReadOnlyList<PatchDownloadRequest>>(), It.IsAny<PatchDownloadSource>(),
                It.IsAny<int>(), It.IsAny<Action<string>>(), It.IsAny<Action<long, long>>(), It.IsAny<CancellationToken>()))
            .Returns([]);
        var configService = new Mock<ILauncherConfigService>();
        configService.SetupGet(service => service.Config).Returns(new LauncherConfig("config.json"));

        var service = new InstallPlanService(fs, cacheService.Object, new MockEnvironmentProvider(),
            new Mock<IAssemblyService>().Object, new Mock<IModuleDefinitionService>().Object, zipFileService.Object,
            diskSpace.Object, patchDownloadService.Object, configService.Object, conversion.Object);
        return (service, cacheService, conversion, calls);
    }

    [Test]
    public async Task Uninstall_RevertsTheBundleConversionBeforeRestoringTheSnapshot()
    {
        var (service, _, _, calls) = MakeService();
        var plan = new InstallPlan();
        plan.Uninstall();

        await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(new[] { "revert", "restore" }));
    }

    [Test]
    public async Task RevertToStockAlone_RevertsTheBundleConversionBeforeRestoringTheSnapshot()
    {
        var (service, _, _, calls) = MakeService();
        var plan = new InstallPlan();
        plan.RevertToStock();

        await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(new[] { "revert", "restore" }));
    }

    [Test]
    public async Task RevertToStockBeforeAPatch_KeepsTheBundleConversion()
    {
        var (service, _, conversion, _) = MakeService();
        var plan = new InstallPlan();
        plan.RevertToStock();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), "corrupt patch");

        // The patch is corrupt, so the plan rolls back, but the conversion must not have been reverted on the way.
        await Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        conversion.Verify(c => c.Revert(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}