using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    [TestCase(false), TestCase(true), Platform("Win")]
    public async Task LegacyUpgrade_HoldsLaunchReservationAndHonorsExplicitReduxRetention(bool retainSettings)
    {
        const string settings = "Redux/settings/player.txt";
        Fixture fixture = NativeFixture();
        if (retainSettings)
            fixture.Entry.FullSourceUnownedDirectories.Add("Redux/settings");
        var service = fixture.Service();
        var events = new List<string>();
        var stock = fixture.Stock with
        {
            SourceRoot = fixture.Install, InstallationId = fixture.Entry.Id,
            UnownedDirectories = fixture.Stock.UnownedDirectories.Concat(fixture.Entry.FullSourceUnownedDirectories).ToArray(),
        };
        fixture.Preparation.Setup(p => p.VerifyStockAsync(stock, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await service.UpgradeLegacyAsync(fixture.Entry.Id,
            () =>
            {
                events.Add("uninstall");
                Assert.Throws<IOException>(() => fixture.Service().AcquireLaunchLease(fixture.Install));
                Put(fixture.Fs, fixture.Install, settings, USER_BYTES);
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
        string settingsPath = At(fixture.Fs, fixture.Install, settings);
        if (retainSettings)
            Assert.That(fixture.Fs.File.ReadAllText(settingsPath), Is.EqualTo(USER_BYTES));
        else
            Assert.That(fixture.Fs.File.Exists(settingsPath), Is.False);
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
