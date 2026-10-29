using System.IO.Compression;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
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
        MakeService(IInstallFileLockService? lockService = null, bool patchesApply = false)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetTempPath());
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(PatchPath)!);
        fs.File.WriteAllBytes(PatchPath, [0x01]);
        fs.File.WriteAllBytes(fs.Path.Combine(InstallDir, "uninstall.zip"), [0x00]);
        if (patchesApply)
        {
            WriteEmptyPatch(fs);
        }

        var calls = new List<string>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(c => c.RecursivelyRestoreCache(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => calls.Add("restore"));
        var conversion = new Mock<IBundleConversionService>();
        conversion.Setup(c => c.Revert(InstallDir, It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("revert"));
        conversion.Setup(c => c.Convert(InstallDir, It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("convert"));

        // Every patch, the prepatch included, fails to open, so a plan that reaches one rolls back.
        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(It.IsAny<string>())).Throws(new IOException("archive is corrupt"));
        if (patchesApply)
        {
            // ZipFile.OpenRead reads the real disk, so the archive is opened from the mock file system instead.
            zipFileService.Setup(z => z.OpenRead(PatchPath)).Returns(() =>
                new ZipFileService(fs).NewArchive(fs.File.OpenRead(PatchPath), ZipArchiveMode.Read, false));
        }
        var assemblyService = new Mock<IAssemblyService>();
        assemblyService.Setup(a => a.GetManifestResourceStream(It.IsAny<string>())).Returns(() => new MemoryStream([0x00]));
        var diskSpace = new Mock<IDiskSpaceService>();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var patchDownloadService = new Mock<IPatchDownloadService>();
        patchDownloadService.Setup(service => service.EnqueueAll(
                It.IsAny<IReadOnlyList<PatchDownloadRequest>>(), It.IsAny<PatchDownloadSource>(),
                It.IsAny<int>(), It.IsAny<Action<LogMessage>>(), It.IsAny<Action<long, long>>(), It.IsAny<CancellationToken>()))
            .Returns([]);
        var configService = new Mock<ILauncherConfigService>();
        configService.SetupGet(service => service.Config).Returns(new LauncherConfig("config.json"));

        var service = new InstallPlanService(fs, cacheService.Object, new MockEnvironmentProvider(),
            assemblyService.Object, new Mock<IModuleDefinitionService>().Object, zipFileService.Object, diskSpace.Object, patchDownloadService.Object, configService.Object, conversion.Object, lockService);
        return (service, cacheService, conversion, calls, fs);
    }

    // A patch that applies and changes nothing, so a plan gets past its patch to the bundle conversion.
    private static void WriteEmptyPatch(MockFileSystem fs)
    {
        using var stream = fs.File.Create(PatchPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
        writer.Write("{\"operations\": []}");
    }

    [Test]
    public async Task AHeldGameFileStopsThePlanBeforeAnythingChanges()
    {
        var lockService = new Mock<IInstallFileLockService>();
        lockService.Setup(l => l.EnsureNoFilesInUse(InstallDir))
            .Throws(new FilesInUseException("A game file is in use.", ["ui_assets_all.bundle"], ["Unity (process 1)"]));
        var (service, _, _, calls, _) = MakeService(lockService.Object);
        var plan = new InstallPlan();
        plan.Uninstall();

        await Assert.ThrowsAsync<FilesInUseException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(calls, Is.Empty);
    }

    [Test]
    public async Task UninstallStopsBeforeTheRestoreWhenABundleCannotBePutBack()
    {
        var (service, _, conversion, calls, _) = MakeService();
        conversion.Setup(c => c.Revert(InstallDir, It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("revert"))
            .Throws(new IOException("A stock bundle could not be put back to stock."));
        var plan = new InstallPlan();
        plan.Uninstall();

        await Assert.ThrowsAsync<IOException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        // The restore deletes the conversion journal, after which the bundle could never be put back.
        Assert.That(calls, Is.EqualTo(new[] { "revert" }));
    }

    [Test]
    public async Task AFailedConversionAfterThePatchRollsTheInstallBack()
    {
        var (service, _, conversion, calls, _) = MakeService(patchesApply: true);
        conversion.Setup(c => c.Convert(InstallDir, It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("convert"))
            .Throws(new IOException("4 stock bundles could not be converted."));
        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), LogMessage.Literal("empty patch"));

        var ex = await Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(ex!.InnerException?.Message, Is.EqualTo("4 stock bundles could not be converted."));
        Assert.That(ex.RolledBack, Is.True);
        Assert.That(calls, Is.EqualTo(new[] { "convert", "revert", "restore" }));
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
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), LogMessage.Literal("corrupt patch"));
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
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), LogMessage.Literal("snapshot patch"));
        plan.Prepatch();
        plan.RevertToStock();

        var ex = await Assert.ThrowsAsync<InstallFailedException>(async () =>
            await service.ApplyToFolder(plan, InstallDir, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));

        // The prepatch itself is the corrupt archive here. Refusing the install as a Redux one stops before it.
        Assert.That(ex!.InnerException, Is.InstanceOf<IOException>());
        Assert.That(ex.InnerException!.Message, Is.EqualTo("archive is corrupt"));
    }
}