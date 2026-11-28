using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class InstallPlanServiceSaveBackupTest
{
    private const string InstallDir = @"C:\Games\Ksp2";
    private const string PatchPath = @"C:\downloads\update.patch";

    private static async Task<(List<string> Order, List<string> Log)> RunPlan(Func<SaveBackup?> backUp)
    {
        List<string> order = [];
        List<string> log = [];
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(InstallDir);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(PatchPath)!);
        fs.File.WriteAllBytes(PatchPath, [0x01]);

        var zipFileService = new Mock<IZipFileService>();
        zipFileService.Setup(z => z.OpenRead(PatchPath))
            .Callback(() => order.Add("patch the game"))
            .Throws(new IOException("stop after the first step starts"));
        var saveBackups = new Mock<ISaveBackupService>();
        saveBackups.Setup(s => s.BackUp(InstallDir, It.IsAny<CancellationToken>())).Returns(() =>
        {
            order.Add("back up saves");
            return backUp();
        });
        var diskSpace = new Mock<IDiskSpaceService>();
        diskSpace.Setup(d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var patchDownloadService = new Mock<IPatchDownloadService>();
        patchDownloadService.Setup(service => service.EnqueueAll(
                It.IsAny<IReadOnlyList<PatchDownloadRequest>>(), It.IsAny<PatchDownloadSource>(),
                It.IsAny<int>(), It.IsAny<Action<LogMessage>>(), It.IsAny<Action<long, long>>(), It.IsAny<CancellationToken>()))
            .Returns([]);
        var configService = new Mock<ILauncherConfigService>();
        configService.SetupGet(service => service.Config).Returns(new LauncherConfig("config.json"));

        var service = new InstallPlanService(fs, new Mock<ICacheService>().Object, new MockEnvironmentProvider(),
            new Mock<IAssemblyService>().Object, new Mock<IModuleDefinitionService>().Object, zipFileService.Object,
            diskSpace.Object, patchDownloadService.Object, configService.Object, Mock.Of<IInstallationStateService>(), saveBackupService: saveBackups.Object);
        var plan = new InstallPlan();
        plan.ApplyPatchFile((_, _, _) => Task.FromResult(PatchPath), LogMessage.Literal("update"));

        await Assert.CatchAsync(() =>
            service.ApplyToFolder(plan, InstallDir, message => log.Add(message.English), (_, _) => { }, (_, _) => { }, CancellationToken.None));
        return (order, log);
    }

    [Test]
    public async Task ApplyToFolder_BacksUpTheSavesBeforeTheFirstStep()
    {
        var (order, log) = await RunPlan(() => new SaveBackup(@"C:\Storage\saves-1.zip", DateTime.Now, 1));

        Assert.That(order, Is.EqualTo(new[] { "back up saves", "patch the game" }));
        Assert.That(log, Has.Some.Contains("saves-1.zip"));
    }

    [Test]
    public async Task ApplyToFolder_BackupFails_ReportsItAndInstallsAnyway()
    {
        var (order, log) = await RunPlan(() => throw new IOException("disk full"));

        Assert.That(order, Is.EqualTo(new[] { "back up saves", "patch the game" }));
        Assert.That(log, Has.Some.Contains("Could not back up your saves").And.Contains("disk full"));
    }
}
