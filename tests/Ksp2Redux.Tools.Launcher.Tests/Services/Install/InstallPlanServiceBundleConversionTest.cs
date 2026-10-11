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

    private static (InstallPlanService Service, Mock<ICacheService> CacheService, Mock<IBundleConversionService> Conversion, List<string> Calls, MockFileSystem Fs)
        MakeService()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetTempPath());
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

        // Every patch, the prepatch included, fails to open, so a plan that reaches one rolls back.
        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(It.IsAny<string>())).Throws(new IOException("archive is corrupt"));
        var assemblyService = new Mock<IAssemblyService>();
        assemblyService.Setup(a => a.GetManifestResourceStream(It.IsAny<string>())).Returns(() => new MemoryStream([0x00]));
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
            assemblyService.Object, new Mock<IModuleDefinitionService>().Object, zipFileService.Object,
            diskSpace.Object, patchDownloadService.Object, configService.Object, conversion.Object);
        return (service, cacheService, conversion, calls, fs);
    }

    [Test]
    public async Task Uninstall_RevertsTheBundleConversionBeforeRestoringTheSnapshot()
    {
        var (service, _, _, calls, _) = MakeService();
        var plan = new InstallPlan();
        plan.Uninstall();

        await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(new[] { "revert", "restore" }));
    }

    [Test]
    public async Task RevertToStockAlone_RevertsTheBundleConversionBeforeRestoringTheSnapshot()
    {
        var (service, _, _, calls, _) = MakeService();
        var plan = new InstallPlan();
        plan.RevertToStock();

        await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(new[] { "revert", "restore" }));
    }

    [Test]
    public async Task RevertToStockBeforeAPatch_KeepsTheBundleConversionUntilTheRollback()
    {
        var (service, _, _, calls, _) = MakeService();
        // Each step goes in front of the ones already planned.
        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), "corrupt patch");
        plan.RevertToStock();

        await Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        // The revert to stock keeps the conversion for the patch. The patch is corrupt, and the rollback leaves
        // a stock install, which converted bundles would draw magenta, so it undoes the conversion.
        Assert.That(calls, Is.EqualTo(new[] { "restore", "revert", "restore" }));
    }

    [Test]
    public async Task RevertToStockThenPrepatch_ReachesThePrepatchWhenTheConversionJournalIsKept()
    {
        var (service, _, _, _, fs) = MakeService();
        // A Steam install that the restore left with the folders a repatch keeps under Redux.
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, Ksp2Install.KSP2_EXE_NAME), [0x00]);
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Plugins"));
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "KSP2_x64_Data", "Plugins", "Steamworks.NET.txt"), "");
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "Config"));
        fs.Directory.CreateDirectory(fs.Path.Combine(InstallDir, "Redux", "BundleConversion"));
        fs.File.WriteAllText(fs.Path.Combine(InstallDir, "Redux", "BundleConversion", "journal.json"), "{}");
        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), "snapshot patch");
        plan.Prepatch();
        plan.RevertToStock();

        var ex = await Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        // The prepatch itself is the corrupt archive here. Refusing the install as a Redux one stops before it.
        Assert.That(ex!.InnerException, Is.InstanceOf<IOException>());
        Assert.That(ex.InnerException!.Message, Is.EqualTo("archive is corrupt"));
    }
}