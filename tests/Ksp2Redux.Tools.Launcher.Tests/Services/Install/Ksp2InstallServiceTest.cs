using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class Ksp2InstallServiceTest
{
    private static (Ksp2InstallService Service, Mock<ILauncherConfigService> Cfg, LauncherConfig Config) MakeService()
    {
        var cfg = new Mock<ILauncherConfigService>();
        var fileSystem = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        var path = new Mock<IPath>();
        file.Setup(f => f.Exists(It.IsAny<string>())).Returns(false);
        path.Setup(p => p.GetFileName(It.IsAny<string>())).Returns<string>(s => s);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);

        var moduleDef = new Mock<IModuleDefinitionService>();
        var logService = new Mock<ILogService>();
        var config = new LauncherConfig { StoragePath = "/tmp/cfg.json" };
        cfg.SetupGet(c => c.Config).Returns(config);
        var svc = new Ksp2InstallService(cfg.Object, fileSystem.Object, moduleDef.Object, logService.Object);
        return (svc, cfg, config);
    }

    [Test]
    public void AddInstall_FirstEntry_BecomesActive_AndPersists()
    {
        var (svc, cfgMock, config) = MakeService();

        var raisedActive = 0;
        var raisedInstalls = 0;
        svc.ActiveInstallChanged += (_, _) => raisedActive++;
        svc.InstallsChanged += (_, _) => raisedInstalls++;

        var entry = svc.AddInstall("/games/KSP2/KSP2_x64.exe");

        Assert.Multiple(() =>
        {
            Assert.That(config.Ksp2Installs, Has.Count.EqualTo(1));
            Assert.That(config.ActiveKsp2InstallId, Is.EqualTo(entry.Id));
            Assert.That(svc.ActiveEntry, Is.SameAs(entry));
            Assert.That(raisedInstalls, Is.EqualTo(1));
            Assert.That(raisedActive, Is.EqualTo(1));
        });
        cfgMock.Verify(c => c.Save(), Times.Once);
    }

    [Test]
    public void AddInstall_SecondEntry_DoesNotChangeActive()
    {
        var (svc, _, config) = MakeService();
        var first = svc.AddInstall("/a/KSP2_x64.exe");

        var raisedActive = 0;
        svc.ActiveInstallChanged += (_, _) => raisedActive++;
        var second = svc.AddInstall("/b/KSP2_x64.exe");

        Assert.That(config.Ksp2Installs, Has.Count.EqualTo(2));
        Assert.That(config.ActiveKsp2InstallId, Is.EqualTo(first.Id));
        Assert.That(raisedActive, Is.Zero);
    }

    [Test]
    public void RemoveInstall_OfActive_FallsBackToFirstRemaining()
    {
        var (svc, _, config) = MakeService();
        var a = svc.AddInstall("/a/KSP2_x64.exe");
        var b = svc.AddInstall("/b/KSP2_x64.exe");
        svc.SetActiveInstall(b.Id);

        var raisedActive = 0;
        svc.ActiveInstallChanged += (_, _) => raisedActive++;
        svc.RemoveInstall(b.Id);

        Assert.That(config.Ksp2Installs, Has.Count.EqualTo(1));
        Assert.That(config.ActiveKsp2InstallId, Is.EqualTo(a.Id));
        Assert.That(raisedActive, Is.EqualTo(1));
    }

    [Test]
    public void RemoveInstall_OfLast_LeavesNoActive()
    {
        var (svc, _, config) = MakeService();
        var only = svc.AddInstall("/a/KSP2_x64.exe");
        svc.RemoveInstall(only.Id);

        Assert.That(config.Ksp2Installs, Is.Empty);
        Assert.That(config.ActiveKsp2InstallId, Is.Null);
        Assert.That(svc.ActiveEntry, Is.Null);
    }

    [Test]
    public void SetActiveInstall_FiresEvent_OnlyWhenChanged()
    {
        var (svc, _, _) = MakeService();
        var a = svc.AddInstall("/a/KSP2_x64.exe");
        var b = svc.AddInstall("/b/KSP2_x64.exe");

        var raised = 0;
        svc.ActiveInstallChanged += (_, _) => raised++;

        svc.SetActiveInstall(a.Id); // already active
        Assert.That(raised, Is.Zero);

        svc.SetActiveInstall(b.Id);
        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public void RenameInstall_UpdatesNameAndFiresInstallsChanged()
    {
        var (svc, _, _) = MakeService();
        var entry = svc.AddInstall("/a/KSP2_x64.exe");

        var raised = 0;
        svc.InstallsChanged += (_, _) => raised++;
        svc.RenameInstall(entry.Id, "My Steam Copy");

        Assert.That(entry.Name, Is.EqualTo("My Steam Copy"));
        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public void UpdateActiveReleaseChannel_UpdatesActiveEntryChannel()
    {
        var (svc, _, _) = MakeService();
        var entry = svc.AddInstall("/a/KSP2_x64.exe");

        var raised = 0;
        svc.ActiveInstallChanged += (_, _) => raised++;
        svc.UpdateActiveReleaseChannel("alpha");

        Assert.That(entry.ReleaseChannel, Is.EqualTo("alpha"));
        Assert.That(raised, Is.EqualTo(1));
    }

    [Test]
    public void AutoSwitchToStableIfPending_SwitchesActiveBetaInstall_Once()
    {
        var (svc, cfgMock, config) = MakeService();
        var entry = svc.AddInstall("/a/KSP2_x64.exe");

        var raised = 0;
        svc.ActiveInstallChanged += (_, _) => raised++;
        svc.AutoSwitchToStableIfPending(true);

        Assert.Multiple(() =>
        {
            Assert.That(entry.ReleaseChannel, Is.EqualTo("stable"));
            Assert.That(config.AutoSwitchedToStable, Is.True);
            Assert.That(raised, Is.EqualTo(1));
        });

        svc.AutoSwitchToStableIfPending(true);
        Assert.That(raised, Is.EqualTo(1), "Second call must be a no-op.");
    }

    [Test]
    public void AutoSwitchToStableIfPending_NoStableRelease_DoesNothing()
    {
        var (svc, cfgMock, config) = MakeService();
        var entry = svc.AddInstall("/a/KSP2_x64.exe");
        cfgMock.Invocations.Clear();

        svc.AutoSwitchToStableIfPending(false);

        Assert.Multiple(() =>
        {
            Assert.That(entry.ReleaseChannel, Is.EqualTo("beta"));
            Assert.That(config.AutoSwitchedToStable, Is.False);
        });
        cfgMock.Verify(c => c.Save(), Times.Never);
    }

    [Test]
    public void AutoSwitchToStableIfPending_NoActiveInstall_Defers()
    {
        var (svc, cfgMock, config) = MakeService();

        svc.AutoSwitchToStableIfPending(true);

        Assert.That(config.AutoSwitchedToStable, Is.False);
        cfgMock.Verify(c => c.Save(), Times.Never);
    }

    [Test]
    public void AutoSwitchToStableIfPending_ActiveOnOtherChannel_SpendsGateWithoutSwitching()
    {
        var (svc, _, config) = MakeService();
        var entry = svc.AddInstall("/a/KSP2_x64.exe");
        svc.UpdateActiveReleaseChannel("alpha");

        var raised = 0;
        svc.ActiveInstallChanged += (_, _) => raised++;
        svc.AutoSwitchToStableIfPending(true);

        Assert.Multiple(() =>
        {
            Assert.That(entry.ReleaseChannel, Is.EqualTo("alpha"));
            Assert.That(config.AutoSwitchedToStable, Is.True);
            Assert.That(raised, Is.Zero);
        });
    }
}
