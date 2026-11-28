using System.Text;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// The release's boot.config settings are published with its generation, and uninstall restores the original file's keys.
/// </summary>
public partial class FullSourceInstallationServiceTest
{
    // The independent test authority accepts the target again once it carries settings.
    private static FullSourcePreparedTarget WithBootSettings(Fixture fixture, FullSourcePreparedTarget target,
        Dictionary<string, string> settings)
    {
        FullSourcePreparedTarget changed = target with { BootConfigSettings = settings };
        fixture.Preparation.Setup(service => service.VerifyTargetAsync(changed, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return changed;
    }

    private static (Ksp2InstallService Installs, InstallationStateService Guard) Installs(Fixture fixture)
    {
        var bootConfig = new BootConfigSettingsService(fixture.Configuration, fixture.Fs, Mock.Of<ILogService>());
        FullSourceInstallationService coordinator = fixture.Service(bootConfig: bootConfig);
        var guard = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        return (new Ksp2InstallService(fixture.Configuration, fixture.Fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), guard, coordinator, bootConfig), guard);
    }

    /// <summary>
    /// A release's settings are published with the generation that commits, an update without settings records none,
    /// and stock has no release settings.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task ReleaseBootSettings_ArePublishedWithTheCommittedGeneration()
    {
        Fixture fixture = Create();
        var settings = new Dictionary<string, string> { ["enable-directstorage"] = "True", ["gc-max-time-slice"] = "3" };

        await Apply(fixture, target: WithBootSettings(fixture, fixture.Target2, settings));
        Assert.That(fixture.Service().GetPublishedBootConfigSettings(fixture.Entry.Id), Is.EqualTo(settings));

        await Apply(fixture, target: fixture.Target3, initial: false);
        Assert.That(fixture.Service().GetPublishedBootConfigSettings(fixture.Entry.Id), Is.Empty,
            "A release without settings records that it has none.");

        await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
        Assert.That(fixture.Service().GetPublishedBootConfigSettings(fixture.Entry.Id), Is.Null);
    }

    /// <summary>
    /// A rolled-back update leaves the settings of the generation that stayed.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task RolledBackUpdate_KeepsThePublishedReleaseSettings()
    {
        Fixture fixture = Create();
        var settings = new Dictionary<string, string> { ["enable-directstorage"] = "True" };
        await Apply(fixture, target: WithBootSettings(fixture, fixture.Target2, settings));
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "stage-create" && !after)
                throw new IOException("Process loss");
        }), fixture.Target3, initial: false));

        Assert.That(fixture.Service().Reconcile(fixture.Entry.Id)!.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        Assert.That(fixture.Service().GetPublishedBootConfigSettings(fixture.Entry.Id), Is.EqualTo(settings));
    }

    /// <summary>
    /// The launcher stops after the game committed and before it saved the release's settings. The next start finds
    /// the settings in the catalog and writes them.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task InterruptedInstall_StartupAppliesTheCommittedReleaseSettings()
    {
        Fixture fixture = Create();
        FullSourcePreparedTarget target = WithBootSettings(fixture, fixture.Target2, new() { ["enable-directstorage"] = "True" });
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && after)
                throw new IOException("Process loss after the engine committed");
        }), target));
        Assert.That(fixture.Entry.BootConfigReleaseSettings, Is.Null);
        var (installs, _) = Installs(fixture);

        installs.TryLoadKsp2Install();
        installs.ApplyActiveInstallBootConfig();

        Assert.That(fixture.Entry.BootConfigReleaseSettings, Is.EqualTo(new Dictionary<string, string> { ["enable-directstorage"] = "True" }));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)),
            Is.EqualTo(STOCK_BOOT + "enable-directstorage=True\r\ngfx-enable-gfx-jobs=0\r\n"));
    }

    /// <summary>
    /// A stale configuration record is replaced by the catalog's, and a release without settings clears it.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StaleConfigurationRecord_IsReplacedByTheCatalogRecord()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        fixture.Entry.BootConfigReleaseSettings = new() { ["old-setting"] = "1" };
        var (installs, _) = Installs(fixture);
        installs.TryLoadKsp2Install();

        installs.ApplyActiveInstallBootConfig();

        Assert.That(fixture.Entry.BootConfigReleaseSettings, Is.Null);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT + "gfx-enable-gfx-jobs=0\r\n"));
    }

    /// <summary>
    /// A catalog written before the settings were recorded clears stale release settings without rewriting the catalog.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CatalogWithoutRecordedSettings_ClearsStaleConfigurationSettings()
    {
        Fixture fixture = Create();
        await Apply(fixture, target: WithBootSettings(fixture, fixture.Target2, new() { ["enable-directstorage"] = "True" }));
        Rewrite(fixture, fixture.Head, catalog => catalog.Remove("ReleaseBootConfig"));
        byte[] head = fixture.Fs.File.ReadAllBytes(fixture.Head);
        fixture.Entry.BootConfigReleaseSettings = new() { ["stale"] = "1" };
        fixture.Entry.BootConfigOriginals = new() { ["stale"] = null };
        string boot = At(fixture.Fs, fixture.Install, BOOT);
        fixture.Fs.File.AppendAllText(boot, "stale=1\r\n");
        var (installs, _) = Installs(fixture);
        installs.TryLoadKsp2Install();

        Assert.That(fixture.Service().GetPublishedBootConfigSettings(fixture.Entry.Id), Is.Null);
        installs.ApplyActiveInstallBootConfig();

        Assert.That(fixture.Entry.BootConfigReleaseSettings, Is.Null);
        Assert.That(fixture.Fs.File.ReadAllText(boot), Is.EqualTo(STOCK_BOOT + "gfx-enable-gfx-jobs=0\r\n"));
        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Head), Is.EqualTo(head));
        fixture.Service().AcquireLaunchLease(fixture.Install)?.Dispose();
        string configurationPath = fixture.Configuration.Config.StoragePath;
        fixture.Fs.File.WriteAllBytes(configurationPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fixture.Configuration.Config));
        Assert.DoesNotThrow(() => new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs))
            .EnsureSelfUpdateAllowedFromFile(configurationPath));
    }

    [Test]
    public async Task RefusedLegacyRecovery_StartupLeavesBootEvidenceIntact()
    {
        Fixture fixture = Create();
        await LegacyPending(fixture, "engine-apply", false);
        Put(fixture.Fs, fixture.Install, ".redux-transaction.active", "legacy engine marker");
        Put(fixture.Fs, fixture.Install, "Redux/legacy-player-marker.txt", "legacy Redux player");
        string boot = At(fixture.Fs, fixture.Install, BOOT);
        fixture.Fs.File.AppendAllText(boot, "gfx-enable-gfx-jobs=1\r\n");
        byte[] evidence = fixture.Fs.File.ReadAllBytes(boot);
        var (installs, guard) = Installs(fixture);
        Assert.Throws<InvalidDataException>(() => guard.EnsureLaunchAllowed(fixture.Install));

        installs.TryLoadKsp2Install();
        Assert.That(installs.Ksp2?.Distribution, Is.EqualTo(Ksp2Redux.Tools.Launcher.Models.Distribution.Redux));
        installs.ApplyActiveInstallBootConfig();

        Assert.That(fixture.Fs.File.ReadAllBytes(boot), Is.EqualTo(evidence));
        Assert.That(fixture.Entry.BootConfigOriginals, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CliLaunch_UsesBootSettingsOfTheSelectedNonActiveProfile(bool managed)
    {
        Fixture fixture = Create();
        if (managed)
        {
            FullSourcePreparedTarget target = WithBootSettings(fixture, fixture.Target2,
                new() { ["enable-directstorage"] = "True" });
            await Apply(fixture, target: target);
        }
        else
        {
            Put(fixture.Fs, fixture.Install, "Redux/legacy-player-marker.txt", "legacy Redux player");
        }

        string activeRoot = At(fixture.Fs, fixture.Root, "active-game");
        Put(fixture.Fs, activeRoot, Ksp2Install.KSP2_EXE_NAME, "active player");
        Put(fixture.Fs, activeRoot, BOOT, STOCK_BOOT);
        var active = new Ksp2InstallEntry { ExePath = At(fixture.Fs, activeRoot, Ksp2Install.KSP2_EXE_NAME) };
        fixture.Configuration.Config.Ksp2Installs.Add(active);
        fixture.Configuration.Config.ActiveKsp2InstallId = active.Id;
        fixture.Entry.LaunchThroughSteam = true;
        fixture.GameProcesses.Setup(service => service.WaitForStartAsync(fixture.Install, It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);

        using var output = new StringWriter();
        int result = await new LaunchCommand().RunWithContextAsync(Context(fixture, output, bootSettings: true),
            new LaunchSettings { Install = fixture.Entry.Id.ToString(), NoBanner = true });

        Assert.That(result, Is.EqualTo(ExitCode.SUCCESS));
        string expected = STOCK_BOOT + (managed ? "enable-directstorage=True\r\n" : "") + "gfx-enable-gfx-jobs=0\r\n";
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(expected));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, activeRoot, BOOT)), Is.EqualTo(STOCK_BOOT));
    }

    /// <summary>
    /// A recorded setting the launcher could not write, such as the build identity, is evidence of a corrupt catalog.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CatalogWithAnInvalidRecordedSetting_IsRefused()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        Rewrite(fixture, fixture.Head, catalog => catalog["ReleaseBootConfig"] = new JsonObject { ["build-guid"] = "other" });

        Assert.Throws<InvalidDataException>(() => fixture.Service().GetPublishedBootConfigSettings(fixture.Entry.Id));
    }

    private const string ARCHIVED_BOOT = "wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\ngc-max-time-slice=3\r\nbuild-guid=stock-guid\r\nextra=5\r\n";
    private const string REDUX_BOOT = "wait-for-native-debugger=0\r\nhdr-display-enabled=1\r\ngc-max-time-slice=3\r\nbuild-guid=redux-guid\r\nextra=9\r\nredux-only=1\r\ngfx-threading-mode=6\r\ngfx-enable-gfx-jobs=1\r\nenable-directstorage=True\r\n";

    [TestCase("catalog-ownership:publish", true, false)]
    [TestCase("engine-apply", true, false)]
    [TestCase("engine-apply", true, true)]
    [TestCase("engine-apply", false, false)]
    public async Task InterruptedLegacyUninstall_RestoresArchivedSettingsOnlyAfterAStockCommit(
        string boundary, bool committed, bool lockedOnRestart)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        WriteLegacyRecords(fixture);
        WriteLegacyArchive(fixture, archivedBootConfig: Encoding.UTF8.GetBytes(ARCHIVED_BOOT));
        string boot = At(fixture.Fs, fixture.Install, BOOT);
        fixture.Fs.File.WriteAllText(boot, REDUX_BOOT);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service((name, after) =>
        {
            if (name == boundary && after == committed)
                throw new IOException("Interrupted during uninstall");
        }).UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));

        var (installs, _) = Installs(fixture);
        installs.TryLoadKsp2Install();
        if (lockedOnRestart)
        {
            using (fixture.Fs.File.Open(boot, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                installs.ApplyActiveInstallBootConfig();
            Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["StockBootConfigRestore"], Is.Not.Null);
        }
        installs.ApplyActiveInstallBootConfig();

        if (committed)
        {
            Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id), Is.Null);
            Assert.That(fixture.Fs.File.ReadAllText(boot), Does.Contain("hdr-display-enabled=0").And.Contain("extra=5")
                .And.Contain("redux-only=1").And.Not.Contain("gfx-threading-mode=").And.Not.Contain("enable-directstorage="));
            Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["StockBootConfigRestore"], Is.Null);
            await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None);
        }
        else
        {
            Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id)!.ReleaseId, Is.EqualTo(fixture.Target2.Identity.ReleaseId));
            Assert.That(fixture.Fs.File.ReadAllText(boot), Does.Contain("hdr-display-enabled=1").And.Contain("extra=9"));
            Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["StockBootConfigRestore"], Is.Null);
        }
    }

    /// <summary>
    /// Uninstalling an installation an earlier launcher converted restores the keys of the original boot.config in its
    /// backup and removes known Redux-only keys the archive did not list. It leaves unrelated keys and the build
    /// identity alone. An entry that is missing, too large or has no usable line changes nothing.
    /// </summary>
    /// <param name="archived">The boot.config in the earlier launcher's backup, or null for a backup without one.</param>
    /// <param name="expected">The boot.config after the uninstall.</param>
    /// <returns>The verification task.</returns>
    [TestCase(ARCHIVED_BOOT,
        "wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\ngc-max-time-slice=3\r\nbuild-guid=redux-guid\r\nextra=5\r\nredux-only=1\r\n")]
    [TestCase(ARCHIVED_BOOT + "gfx-threading-mode=3\r\n",
        "wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\ngc-max-time-slice=3\r\nbuild-guid=redux-guid\r\nextra=5\r\nredux-only=1\r\ngfx-threading-mode=3\r\n")]
    [TestCase(null, REDUX_BOOT)]
    [TestCase("oversized", REDUX_BOOT)]
    [TestCase("unusable", REDUX_BOOT)]
    public async Task UninstallOfAnEarlierLauncherInstall_RestoresTheArchivedBootConfigKeys(string? archived, string expected)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        WriteLegacyRecords(fixture);
        if (archived is not null)
        {
            byte[] bytes = archived switch
            {
                "oversized" => Encoding.UTF8.GetBytes("hdr-display-enabled=0\r\n" + new string('x', 64 * 1024)),
                "unusable" => [0xFF, 0xFE, 0x00, 0x01, 0x80],
                _ => Encoding.UTF8.GetBytes(archived)
            };
            WriteLegacyArchive(fixture, archivedBootConfig: bytes);
        }
        fixture.Fs.File.WriteAllBytes(At(fixture.Fs, fixture.Install, BOOT), Encoding.UTF8.GetBytes(REDUX_BOOT));
        using var output = new StringWriter();
        CliContext context = Context(fixture, output, bootSettings: true);
        var plan = new InstallPlan();
        plan.Uninstall();

        await context.InstallPlanService.ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(expected));
        Assert.That(fixture.Entry.BootConfigOriginals, Is.Empty);
        Assert.That(fixture.Service().HasCatalog(fixture.Install), Is.False);
    }
}
