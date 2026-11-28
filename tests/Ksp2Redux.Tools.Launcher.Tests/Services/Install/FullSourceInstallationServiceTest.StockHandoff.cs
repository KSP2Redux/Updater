using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    /// <summary>
    /// The launcher and full-source CLI return a managed installation to stock and permit a fresh feed-selected install.
    /// </summary>
    /// <param name="useFullSourceCli">Whether to enter through the full-source CLI.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public Task UninstallPlan_ReturnsManagedInstallToStockAndAllowsFreshInstallAsync(bool useFullSourceCli) =>
        VerifyStockHandoffAndReinstallAsync(Create(), useFullSourceCli);

    [Test, Platform("Win")]
    public Task NativeStockHandoff_ReleasesStockAndAllowsFreshInstallAsync() =>
        VerifyStockHandoffAndReinstallAsync(NativeFixture(), useFullSourceCli: false);

    private static async Task VerifyStockHandoffAndReinstallAsync(Fixture fixture, bool useFullSourceCli)
    {
        using var output = new StringWriter();
        CliContext context = Context(fixture, output, bootSettings: true);

        Task ApplyPlanAsync(InstallPlan plan) =>
            context.InstallPlanService.ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        await ApplyPlanAsync(new InstallPlan
        {
            FullSourceInstallationId = fixture.Entry.Id,
            FullSourceTarget = fixture.Target2,
            FullSourceStock = fixture.Stock,
        });
        byte[] originalBackup = fixture.Fs.File.ReadAllBytes(fixture.Archive);

        if (useFullSourceCli)
        {
            int result = await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
            {
                Operation = "uninstall", AssumeYes = true
            });
            Assert.That(result, Is.EqualTo(ExitCode.SUCCESS));
            using JsonDocument json = JsonDocument.Parse(output.ToString());
            Assert.That(json.RootElement.GetProperty("generation").GetString(), Is.EqualTo(fixture.Stock.Stock.BaselineIdentity));
            Assert.That(json.RootElement.GetProperty("result").GetString(), Is.EqualTo("Committed"));
        }
        else
        {
            var uninstall = new InstallPlan();
            uninstall.Uninstall();
            await ApplyPlanAsync(uninstall);
        }

        foreach (var file in fixture.Stock.Stock.Files)
        {
            Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, file.Path)),
                Is.EqualTo(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Stock.SourceRoot, file.Path))), file.Path);
        }
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, "added.bin")), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        Preserved(fixture);

        var coordinator = fixture.Service();
        Assert.That(coordinator.HasCatalog(fixture.Install), Is.False);
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        Assert.DoesNotThrow(() => state.EnsureLegacyMutationAllowed(fixture.Install));

        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string retiredBackup = fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single();
        Assert.That(fixture.Fs.File.ReadAllBytes(retiredBackup), Is.EqualTo(originalBackup));

        var reinstall = new InstallPlan
        {
            FullSourceReleaseId = fixture.Target3.Identity.ReleaseId,
            FullSourceChannel = fixture.Target3.Identity.Channel,
            FullSourceVersion = fixture.Target3.Identity.Version,
            FullSourceManifestSha256 = fixture.Target3.Identity.ManifestSha256,
        };
        fixture.Preparation.Setup(service => service.FetchSelectedPlanAsync(reinstall, fixture.Entry.Id, false, null,
            It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>())).ReturnsAsync((fixture.Target3, fixture.Stock));
        var reinstallTarget = fixture.Target3 with { MayConsumeSource = true };
        fixture.Preparation.Setup(service => service.VerifyTargetAsync(reinstallTarget, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ApplyPlanAsync(reinstall);

        Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target3.Identity));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Archive), Is.EqualTo(originalBackup));
        Assert.That(fixture.Fs.File.Exists(retiredBackup), Is.False);
        Preserved(fixture);

        var secondUninstall = new InstallPlan();
        secondUninstall.Uninstall();
        await ApplyPlanAsync(secondUninstall);
        Assert.That(fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single(), Is.EqualTo(retiredBackup));
        Assert.That(fixture.Fs.File.ReadAllBytes(retiredBackup), Is.EqualTo(originalBackup));
        Assert.That(coordinator.HasCatalog(fixture.Install), Is.False);

        if (fixture.Fs is RealFileSystem)
        {
            var cache = new CacheService(fixture.Fs, new ZipFileService(fixture.Fs), state);
            cache.RecursivelyCreateCache(fixture.Install);
            Put(fixture.Fs, fixture.Install, Ksp2Install.KSP2_EXE_NAME, "simulated legacy patch");
            cache.RecursivelyRestoreCache(fixture.Install);
            Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
            Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Entry.ExePath),
                Is.EqualTo(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Stock.SourceRoot, Ksp2Install.KSP2_EXE_NAME))));
            Assert.That(fixture.Fs.File.ReadAllBytes(retiredBackup), Is.EqualTo(originalBackup));
            Preserved(fixture);
        }
    }

    [Test]
    public async Task Recover_InterruptedAfterMovingBackup_BlocksLegacyAndResumesFromVerifiedStock()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] originalBackup = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        var interruptedRetirement = fixture.Service(engineFault: (operation, after) =>
        {
            if (operation == "retirement-complete:write" && !after)
                throw new IOException("Launcher exited with a torn retirement receipt.");
        });
        await Assert.ThrowsAsync<IOException>(() =>
            interruptedRetirement.UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Archive), Is.EqualTo(originalBackup));
        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string tornReceipt = fixture.Fs.Directory.EnumerateFiles(storage, "payload-retirement.complete", SearchOption.AllDirectories)
            .Single(path => fixture.Fs.FileInfo.New(path).Length == 0);

        var interrupted = fixture.Service((operation, after) =>
        {
            if (operation == "release-stock-backup" && after)
                throw new IOException("Launcher exited after moving the backup.");
        });

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install));

        var restarted = fixture.Service();
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), restarted);
        Assert.Throws<IOException>(() => state.EnsureLegacyMutationAllowed(fixture.Install));
        using (state.AcquireLaunchLease(fixture.Install)) { }
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.True);
        Assert.That(fixture.Fs.FileInfo.New(tornReceipt).Length, Is.GreaterThan(0));

        string retiredBackup = fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single();
        fixture.Fs.File.WriteAllBytes(retiredBackup, new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            restarted.UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install));
        Assert.Throws<IOException>(() => state.EnsureLegacyMutationAllowed(fixture.Install));
        Assert.That(fixture.Fs.File.ReadAllBytes(retiredBackup), Is.EqualTo(new byte[] { 1, 2, 3 }));

        fixture.Fs.File.WriteAllBytes(retiredBackup, originalBackup);
        using var output = new StringWriter();
        Assert.That(await new FullSourceCommand().RunWithContextAsync(Context(fixture, output), new FullSourceSettings
        {
            Operation = "recover", Install = fixture.Entry.Id.ToString()
        }), Is.EqualTo(ExitCode.SUCCESS), output.ToString());
        Assert.That(restarted.HasCatalog(fixture.Install), Is.False);
        Assert.DoesNotThrow(() => state.EnsureLegacyMutationAllowed(fixture.Install));
        Preserved(fixture);
    }

    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task StockHandoff_LearnedDirectoriesAreReusableOnlyByTheOriginalInstallation(bool sameProfile,
        bool configureCaseAlias)
    {
        Fixture fixture = Create();
        const string releaseFile = "Redux/managed.bin";
        const string releaseContents = "published Redux bytes";
        const string userFile = "Redux/config.json";
        Put(fixture.Fs, fixture.Target2.SourceRoot, releaseFile, releaseContents);
        var original = fixture.Target2.Manifest;
        var manifest = new StockBaselineManifest(original.BaselineIdentity, original.Provenance,
            original.Files.Append(new StockBaselineFile(releaseFile, releaseContents.Length,
                Digest(Encoding.UTF8.GetBytes(releaseContents)))), original.Directories.Append("Redux"));
        var target = fixture.Target2 with
        {
            Identity = fixture.Target2.Identity with { ManifestSha256 = Digest(manifest.ToJson()) },
            Manifest = manifest
        };
        fixture.Preparation.Setup(service => service.VerifyTargetAsync(target, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await Apply(fixture, target: target);
        Put(fixture.Fs, fixture.Install, userFile, USER_BYTES);

        await fixture.Service().UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, releaseFile)), Is.False);

        if (sameProfile)
        {
            Fixture reinstall = fixture;
            if (configureCaseAlias)
            {
                var stock = fixture.Stock with { UnownedDirectories = [.. fixture.Stock.UnownedDirectories, "redux"] };
                fixture.Preparation.Setup(service => service.VerifyStockAsync(stock, It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                reinstall = fixture with { Stock = stock };
            }
            await Apply(reinstall, target: target);
            Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, releaseFile)), Is.EqualTo(releaseContents));
        }
        else
        {
            // A separate profile must make its own decision about the occupied path.
            var newEntry = new Ksp2InstallEntry { ExePath = fixture.Entry.ExePath };
            fixture.Configuration.Config.Ksp2Installs = [newEntry];
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Apply(fixture with { Entry = newEntry }, target: target));
            Assert.That(failure?.Message, Does.Contain("unowned directory"));
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, releaseFile)), Is.False);
        }
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, userFile)), Is.EqualTo(USER_BYTES));
    }

    [Test]
    public async Task Reinstall_InterruptedAfterReusingBackup_RecoversWithoutLeavingAnotherArchive()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] originalBackup = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        await fixture.Service().UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install);
        var interrupted = fixture.Service((operation, after) =>
        {
            if (operation == "reuse-released-stock-backup" && after)
                throw new IOException("Launcher exited after recovering the original archive.");
        });

        await Assert.ThrowsAsync<IOException>(() => Apply(fixture, interrupted));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Archive), Is.EqualTo(originalBackup));
        var resumed = fixture.Service();
        Assert.That(resumed.Reconcile(fixture.Entry.Id)?.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        await Apply(fixture, resumed);
        Assert.That(resumed.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));

        await resumed.UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install);
        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string retainedBackup = fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single();
        Assert.That(fixture.Fs.File.ReadAllBytes(retainedBackup), Is.EqualTo(originalBackup));
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
    }

    [Test]
    public async Task StockHandoff_RetargetedProfileKeepsBothRootsIndependent()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        await fixture.Service().UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install);
        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string firstBackup = fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single();
        byte[] firstBackupBytes = fixture.Fs.File.ReadAllBytes(firstBackup);

        string secondRoot = fixture.Fs.Path.Combine(fixture.Root, "another-game");
        foreach (string path in fixture.Stock.Stock.Directories)
            fixture.Fs.Directory.CreateDirectory(At(fixture.Fs, secondRoot, path));
        foreach (StockBaselineFile file in fixture.Stock.Stock.Files)
            Put(fixture.Fs, secondRoot, file.Path, fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Stock.SourceRoot, file.Path)));
        Put(fixture.Fs, secondRoot, RETAINED, "explicit retained bytes");
        fixture.Entry.ExePath = fixture.Fs.Path.Combine(secondRoot, Ksp2Install.KSP2_EXE_NAME);
        var retargeted = fixture with { Install = secondRoot };

        await Apply(retargeted);
        Assert.That(retargeted.Service().GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
        await retargeted.Service().UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, secondRoot);

        Assert.That(fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Count(), Is.EqualTo(2));
        Assert.That(fixture.Fs.File.ReadAllBytes(firstBackup), Is.EqualTo(firstBackupBytes));
        Assert.That(fixture.Fs.File.Exists(retargeted.Archive), Is.False);
    }

    [Test]
    public async Task StockHandoff_WindowsCaseOnlyRootRenameReusesStockEvidence()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        await fixture.Service().UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, fixture.Install);
        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        string retainedBackup = fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single();
        byte[] expected = fixture.Fs.File.ReadAllBytes(retainedBackup);

        string intermediate = fixture.Fs.Path.Combine(fixture.Root, "game-renaming");
        string recased = fixture.Fs.Path.Combine(fixture.Root, "Game");
        fixture.Fs.Directory.Move(fixture.Install, intermediate);
        fixture.Fs.Directory.Move(intermediate, recased);
        fixture.Entry.ExePath = fixture.Fs.Path.Combine(recased, Ksp2Install.KSP2_EXE_NAME);
        var corrected = fixture with { Install = recased };

        await Apply(corrected);
        Assert.That(fixture.Fs.File.ReadAllBytes(corrected.Archive), Is.EqualTo(expected));
        Assert.That(fixture.Fs.File.Exists(retainedBackup), Is.False);
        await corrected.Service().UninstallToStockAsync(fixture.Entry.Id, _ => { }, CancellationToken.None, recased);
        Assert.That(fixture.Fs.Directory.EnumerateFiles(storage, "*-released-stock.zip").Single(), Is.EqualTo(retainedBackup));
    }
}
