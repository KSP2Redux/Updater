using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using System.Text;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    [TestCase(false, false, false), TestCase(true, false, false), TestCase(false, true, false), TestCase(true, true, false), TestCase(false, false, true)]
    public async Task LegacyUpgrade_SelectedPlanUninstallsBeforeStockVerification(bool damageStock, bool viaCli, bool badAuthority)
    {
        Fixture fixture = Create();
        PutLegacyBackup(fixture);
        Put(fixture.Fs, fixture.Install, "Redux/legacy.bin", "legacy addition"u8.ToArray());
        Put(fixture.Fs, fixture.Install, BUNDLE, "legacy converted bytes"u8.ToArray());
        using var services = (ServiceProvider)Services(fixture);
        var cache = Mock.Get(services.GetRequiredService<ICacheService>());
        bool uninstalled = false;
        cache.Setup(c => c.RecursivelyRestoreCache(fixture.Install, false)).Callback(() =>
        {
            uninstalled = true;
            foreach (var file in fixture.Stock.Files)
                Put(fixture.Fs, fixture.Install, file.Path, fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.StockRoot, file.Path)));
            fixture.Fs.Directory.Delete(At(fixture.Fs, fixture.Install, "Redux"), true);
            fixture.Fs.File.Delete(BackupPath(fixture));
            if (damageStock) Put(fixture.Fs, fixture.Install, BUNDLE, "unrestored bytes"u8.ToArray());
            if (badAuthority)
                fixture.Transport.Setup(t => t.GetTrustedManifestAsync(It.IsAny<FeedInfo>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidDataException("Publisher contract invalid."));
        });
        var plan = new InstallPlan { FullSourceReleaseId = RELEASE_ID, FullSourceChannel = CHANNEL, FullSourceVersion = VERSION };
        var service = services.GetRequiredService<IInstallPlanService>();
        async Task Run()
        {
            if (viaCli)
            {
                using var output = new StringWriter();
                var context = new CliContext(services, new CliOutput(output, isJson: true));
                Assert.That(await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
                    { Operation = "apply", Release = RELEASE_ID, Channel = CHANNEL, Install = fixture.Entry.Id.ToString(), NoUpdateCheck = true }, CancellationToken.None), Is.Zero);
            }
            else
                await service.ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        }
        if (damageStock || badAuthority)
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await Run());
            if (badAuthority) Assert.That(failure!.Message, Is.EqualTo("Publisher contract invalid."));
            else Assert.That(failure!.Message, Does.Contain("Verify integrity"));
            Assert.That(fixture.Requests, Is.Empty, "No recipe download follows failed stock verification.");
            Assert.That(fixture.Coordinator().HasCatalog(fixture.Install), Is.False);
        }
        else
        {
            await Run();
            AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        }
        Assert.That(uninstalled, Is.True);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
    }
}
