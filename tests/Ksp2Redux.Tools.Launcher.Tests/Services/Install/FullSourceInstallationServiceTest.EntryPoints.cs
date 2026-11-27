using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    /// <summary>
    /// Source installation and uninstall need neither shader manifests nor conversion journals.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task SourcePlanInstallsAndUninstallsWithoutLegacyConversion()
    {
        Fixture fixture = Create();
        using var output = new StringWriter();
        IInstallPlanService service = Context(fixture, output).InstallPlanService;
        var plan = new InstallPlan
        {
            FullSourceInstallationId = fixture.Entry.Id,
            FullSourceTarget = fixture.Target2,
            FullSourceStock = fixture.Stock
        };
        await service.ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, "Redux/BundleConversion/journal.json")), Is.False);
        string journal = At(fixture.Fs, fixture.Install, "Redux/BundleConversion/journal.json");
        fixture.Fs.Directory.CreateDirectory(fixture.Fs.Path.GetDirectoryName(journal)!);
        fixture.Fs.File.WriteAllText(journal, "stale unowned journal");
        await service.ApplyToFolder(new InstallPlan
        {
            FullSourceInstallationId = fixture.Entry.Id,
            FullSourceTarget = fixture.Target3
        }, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        var uninstall = new InstallPlan { Steps = [new InstallPlan.UninstallStep()] };
        await service.ApplyToFolder(uninstall, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
        Assert.That(fixture.Fs.File.ReadAllText(journal), Is.EqualTo("stale unowned journal"));
    }

    /// <summary>
    /// Without a full-source catalog the command fails before any uninstall runs, rather than uninstalling a legacy install.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CliUninstallWithoutCatalog_FailsBeforeAnyUninstallRuns()
    {
        Fixture fixture = Create();
        using var output = new StringWriter();
        CliContext context = Context(fixture, output);
        Put(fixture.Fs, fixture.Install, "Redux/legacy-marker.txt", "legacy redux bytes");

        int result = await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "uninstall", AssumeYes = true, Install = fixture.Entry.Id.ToString()
        });

        Assert.That(result, Is.EqualTo((int)ExitCode.INSTALL_NOT_FOUND));
        Assert.That(output.ToString(), Does.Contain("no full-source catalog"));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "Redux/legacy-marker.txt")), Is.EqualTo("legacy redux bytes"));
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, "uninstall.zip")), Is.False);
    }

    private static CliContext Context(Fixture fixture, StringWriter output, bool bootSettings = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Configuration);
        services.AddSingleton(fixture.Fs);
        services.AddSingleton<IZipFileService>(new ZipFileService(fixture.Fs));
        services.AddSingleton(fixture.Space);
        services.AddSingleton(fixture.Os);
        services.AddSingleton(fixture.Preparation.Object);
        services.AddSingleton(fixture.GameProcesses.Object);
        services.AddSingleton<IFullSourceInstallationService>(provider =>
            fixture.Service(bootConfig: provider.GetService<IBootConfigSettingsService>()));
        services.AddSingleton<IInstallationStateService, InstallationStateService>();
        services.AddSingleton<IKsp2InstallService, Ksp2InstallService>();
        if (bootSettings)
            services.AddSingleton<IBootConfigSettingsService, BootConfigSettingsService>();
        services.AddSingleton<IInstallPlanService, InstallPlanService>();
        services.AddSingleton(new Mock<IBundleConversionService>(MockBehavior.Strict).Object);
        services.AddSingleton(Mock.Of<ILogService>());
        services.AddSingleton(Mock.Of<IModuleDefinitionService>());
        services.AddSingleton(new Mock<ICacheService>(MockBehavior.Strict).Object);
        services.AddSingleton(new Mock<IPatchDownloadService>(MockBehavior.Strict).Object);
        services.AddSingleton(Mock.Of<IEnvironmentProvider>());
        services.AddSingleton(Mock.Of<IEnvironmentVariableProvider>());
        services.AddSingleton(Mock.Of<IAssemblyService>());
        services.AddSingleton(Mock.Of<IReleasesFeedService>());
        services.AddSingleton(Mock.Of<IKsp2DetectorService>());
        services.AddSingleton(Mock.Of<IGameDataFolderService>());
        services.AddSingleton(Mock.Of<IKsp2GameUninstallService>());
        services.AddSingleton(Mock.Of<IWineRuntimeService>());
        services.AddSingleton(Mock.Of<ISteamSessionService>());
        services.AddSingleton(Mock.Of<ISteamDepotDownloader>());
        services.AddSingleton(Mock.Of<INewsProviderService>());
        services.AddSingleton(Mock.Of<IManifestReleasesFeedProviderService>());
        return new CliContext(services.BuildServiceProvider(), new CliOutput(output, isJson: true));
    }

    /// <summary>
    /// Actual CLI and install-plan entry points fetch independently pinned inputs and use the shared coordinator.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task ProductionCliAndSharedPlan_ApplyRepairChannelSwitchAndLegacyUninstallEntry()
    {
        Fixture fixture = Create();
        fixture.Preparation.Setup(service => service.FetchInitialInstallAsync("release-two", "beta", null, fixture.Entry.Id,
                It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>()))
            .ReturnsAsync((fixture.Target2, fixture.Stock));
        fixture.Preparation.Setup(service => service.FetchTargetForInstalledReleaseAsync("release-three", "internal", null,
                fixture.Entry.Id, fixture.Target2.Identity, It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>()))
            .ReturnsAsync(fixture.Target3);
        fixture.Preparation.Setup(service => service.FetchRepairTargetAsync("release-three", "internal", null,
                fixture.Entry.Id, It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>()))
            .ReturnsAsync(fixture.Target3);
        using var output = new StringWriter();
        CliContext context = Context(fixture, output, bootSettings: true);
        Assert.That(await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "apply", Release = "release-two", Channel = "beta", Install = fixture.Entry.Id.ToString()
        }), Is.Zero);
        Assert.That(context.ReadInstall(fixture.Entry.ExePath)!.GameVersion!.BuildNumber, Is.EqualTo("200"));
        // The settings step ran: the file carries the launcher's line, and the original of that line is on record.
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT + "gfx-enable-gfx-jobs=0\r\n"));
        Assert.That(fixture.Entry.BootConfigOriginals, Is.EquivalentTo(new Dictionary<string, string?> { ["gfx-enable-gfx-jobs"] = null }));
        Assert.That(await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "apply", Release = "release-three", Channel = "internal"
        }), Is.Zero);
        Assert.That(fixture.Entry.LastInstalledVersion!.BuildNumber, Is.EqualTo("300"));
        Put(fixture.Fs, fixture.Install, BUNDLE, "explicit CLI repair damage");
        Assert.That(await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "repair", Release = "release-three", Channel = "internal"
        }), Is.Zero);
        context.InstallService.TryLoadKsp2Install();
        Assert.That(context.InstallService.Ksp2!.Distribution, Is.EqualTo(Ksp2Redux.Tools.Launcher.Models.Distribution.Redux));
        Assert.That(await new UninstallCommand().RunWithContextAsync(context, new UninstallSettings { AssumeYes = true }), Is.Zero);
        Assert.That(fixture.Service().HasCatalog(fixture.Install), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        Preserved(fixture);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
        Assert.That(fixture.Entry.BootConfigOriginals, Is.Empty);
    }

    /// <summary>
    /// Startup and launch guard reconciliation sees pending catalogs after marker cleanup and preserves verified boot bytes.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StartupWithoutEngineMarker_RecoversPublicationBeforeDisplayAndBootSettings()
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && after)
                throw new IOException("Process loss before ownership publication");
        })));
        var coordinator = fixture.Service();
        var guard = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var installs = new Ksp2InstallService(fixture.Configuration, fixture.Fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), guard, coordinator,
            new BootConfigSettingsService(fixture.Configuration, fixture.Fs, Mock.Of<ILogService>()));
        installs.TryLoadKsp2Install();
        Assert.That(installs.Ksp2!.GameVersion!.BuildNumber, Is.EqualTo("200"));
        Assert.That(installs.Ksp2.VersionDetectionException, Is.Null);
        installs.ApplyActiveInstallBootConfig();
        guard.EnsureLaunchAllowed(fixture.Install);
        // The settings step edits the file outside the recovered generation, which still verifies below.
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)),
            Is.EqualTo(STOCK_BOOT + "gfx-enable-gfx-jobs=0\r\n"));
        Assert.Throws<IOException>(() => guard.EnsureLegacyMutationAllowed(fixture.Install));
        Generation(fixture, coordinator.Reconcile(fixture.Entry.Id)!);
    }

    /// <summary>
    /// Production defaults refuse unauthenticated source claims and leave stock installation bytes unchanged.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task MissingIndependentReleaseProvider_CannotTurnLocalManifestIntoProductionAuthority()
    {
        Fixture fixture = Create();
        var service = new FullSourceInstallationService(fixture.Fs, fixture.Configuration, new ZipFileService(fixture.Fs),
            fixture.Space, new UnavailableFullSourcePreparationService(), fixture.Os, Mock.Of<ILogService>(),
            fixture.GameProcesses.Object, new InstallFileLockService(fixture.Fs, fixture.Os),
            engine: new Ksp2Redux.Tools.Common.Transactions.InstallTransactionEngine(fixture.Fs, _ => long.MaxValue, validateFileStreams: _ => { }));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await Apply(fixture, service));
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        Generation(fixture, new FullSourceInstallationStatus(fixture.Entry.Id, fixture.Stock.Stock, null, null, null));
    }

    /// <summary>
    /// Cancellation during preparation rolls back and later cancellation cannot undo a durable commit.
    /// </summary>
    /// <param name="afterCommit">True to cancel after the engine committed, false during staging.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationBoundary_PublishesOnlyVerifiedTerminalOutcome(bool afterCommit)
    {
        Fixture fixture = Create();
        using var cancellation = new CancellationTokenSource();
        var service = fixture.Service((name, after) =>
        {
            if (after && name == (afterCommit ? "engine-apply" : "stage-create"))
                cancellation.Cancel();
        });
        if (afterCommit)
        {
            Generation(fixture, await Apply(fixture, service, ct: cancellation.Token));
            Assert.That(fixture.Service().Reconcile(fixture.Entry.Id)!.Release, Is.EqualTo(fixture.Target2.Identity));
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await Apply(fixture, service, ct: cancellation.Token));
            FullSourceInstallationStatus recovered = fixture.Service().Reconcile(fixture.Entry.Id)!;
            Assert.That(recovered.Release, Is.Null);
            Generation(fixture, recovered);
            Generation(fixture, await Apply(fixture));
        }
    }

    /// <summary>
    /// A second coordinator cannot reconcile or mutate while the first holds the configured installation lease.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CompetingCoordinator_RejectsBeforeSecondMutation()
    {
        Fixture fixture = Create();
        bool raced = false;
        var service = fixture.Service((name, after) =>
        {
            if (name != "engine-apply" || after)
                return;
            raced = true;
            Assert.Throws<IOException>(() => fixture.Service().Reconcile(fixture.Entry.Id));
        });
        Generation(fixture, await Apply(fixture, service));
        Assert.That(raced, Is.True);
    }

    /// <summary>
    /// Changed catalog evidence after commit is never overwritten by ownership publication.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CatalogChangedDuringCommit_RetainsForeignBytesAndVerifiedEngineEvidence()
    {
        Fixture fixture = Create();
        byte[]? trustedHead = null;
        var service = fixture.Service((name, after) =>
        {
            if (name != "engine-apply" || !after)
                return;
            trustedHead = fixture.Fs.File.ReadAllBytes(fixture.Head);
            fixture.Fs.File.WriteAllText(fixture.Head, "foreign catalog evidence after commit");
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture, service));
        Assert.That(fixture.Fs.File.ReadAllText(fixture.Head), Is.EqualTo("foreign catalog evidence after commit"));
        Assert.Throws<InvalidDataException>(() => fixture.Service().Reconcile(fixture.Entry.Id));
        fixture.Fs.File.WriteAllBytes(fixture.Head, trustedHead!);
        FullSourceInstallationStatus recovered = fixture.Service().Reconcile(fixture.Entry.Id)!;
        Assert.That(recovered.Release, Is.EqualTo(fixture.Target2.Identity));
        Generation(fixture, recovered);
    }

    /// <summary>
    /// Early self-update sees catalog intent after the engine already cleared its marker.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task EarlyUpdaterWithoutMarker_BlocksPendingCatalogThenAllowsPublishedCatalog()
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && after)
                throw new IOException("Crash before catalog ownership publication");
        })));
        string configurationPath = fixture.Configuration.Config.StoragePath;
        fixture.Fs.File.WriteAllBytes(configurationPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fixture.Configuration.Config));
        var earlyGuard = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs));
        Assert.Throws<IOException>(() => earlyGuard.EnsureSelfUpdateAllowedFromFile(configurationPath));
        fixture.Service().Reconcile(fixture.Entry.Id);
        Assert.DoesNotThrow(() => earlyGuard.EnsureSelfUpdateAllowedFromFile(configurationPath));
    }

    /// <summary>
    /// Unsupported new host transactions fail before mutation while catalog-free legacy paths remain usable.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task PosixAdapterBoundary_RejectsFullSourceAndLeavesLegacyHostPathAvailable()
    {
        Fixture fixture = Create();
        var os = new Mock<IOperatingSystemService>();
        os.Setup(service => service.IsWindows()).Returns(false);
        var unsupported = new FullSourceInstallationService(fixture.Fs, fixture.Configuration, new ZipFileService(fixture.Fs),
            fixture.Space, fixture.Preparation.Object, os.Object, Mock.Of<ILogService>(),
            fixture.GameProcesses.Object, new InstallFileLockService(fixture.Fs, os.Object));
        Assert.That(unsupported.HasCatalog(fixture.Install), Is.False);
        Assert.DoesNotThrow(() => unsupported.EnsureReady(fixture.Install));
        Assert.That(unsupported.AcquireLaunchLease(fixture.Install), Is.Null);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await Apply(fixture, unsupported));
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        await Apply(fixture);
        Assert.Throws<PlatformNotSupportedException>(() => unsupported.Reconcile(fixture.Entry.Id));
        Assert.Throws<PlatformNotSupportedException>(() => unsupported.AcquireLaunchLease(fixture.Install));
    }
}
