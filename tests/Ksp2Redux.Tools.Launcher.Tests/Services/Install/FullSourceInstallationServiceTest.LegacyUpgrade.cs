using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    [Test, Platform("Win")]
    public async Task LegacyUpgrade_HoldsLaunchReservationThroughUninstallAndPreparation()
    {
        Fixture fixture = NativeFixture();
        var service = fixture.Service();
        var events = new List<string>();
        var stock = fixture.Stock with { SourceRoot = fixture.Install, InstallationId = fixture.Entry.Id };
        fixture.Preparation.Setup(p => p.VerifyStockAsync(stock, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await service.UpgradeLegacyAsync(fixture.Entry.Id,
            () =>
            {
                events.Add("uninstall");
                Assert.Throws<IOException>(() => fixture.Service().AcquireLaunchLease(fixture.Install));
                return Task.CompletedTask;
            },
            () =>
            {
                events.Add("prepare");
                Assert.Throws<IOException>(() => fixture.Service().AcquireLaunchLease(fixture.Install));
                return Task.FromResult<(FullSourcePreparedTarget, FullSourceStockPreparation?)>((fixture.Target2, stock));
            }, _ => { }, CancellationToken.None, fixture.Install);
        Assert.That(events, Is.EqualTo(new[] { "uninstall", "prepare" }));
        Assert.That(service.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
    }

    [Test, Platform("Win")]
    public async Task LegacyUpgrade_RefusesDamagedStockBeforePublishingOrApplying()
    {
        Fixture fixture = NativeFixture();
        var stock = fixture.Stock with { SourceRoot = fixture.Install, InstallationId = fixture.Entry.Id };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Service().UpgradeLegacyAsync(fixture.Entry.Id,
                () =>
                {
                    Put(fixture.Fs, fixture.Install, BUNDLE, "unrestored legacy edit");
                    return Task.CompletedTask;
                },
                () => Task.FromResult<(FullSourcePreparedTarget, FullSourceStockPreparation?)>((fixture.Target2, stock)),
                _ => { }, CancellationToken.None, fixture.Install));
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BUNDLE)), Is.EqualTo("unrestored legacy edit"));
    }
}
