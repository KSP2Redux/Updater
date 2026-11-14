using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Common.Transactions;
using System.Diagnostics;
using Avalonia.Headless.NUnit;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Avalonia.Controls;
using MsBox.Avalonia.Enums;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    private static Dictionary<string, string> LiveUseSnapshot(Fixture fixture) => fixture.Fs.Directory
        .EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories)
        .Where(path => fixture.Fs.Path.GetFileName(path) != "coordinator.lock")
        .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path)), StringComparer.Ordinal);

    private static void LiveUseUnchanged(Fixture fixture, Dictionary<string, string> before)
    {
        Assert.That(LiveUseSnapshot(fixture), Is.EquivalentTo(before), "Game, archive, catalog and retained evidence must be unchanged.");
        Preserved(fixture);
        fixture.GameProcesses.Verify(service => service.StopAsync(), Times.Never);
    }

    /// <summary>
    /// Direct conversion rejects a running intended game before creating backup or catalog authority.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_RunningGameRejectsDirectInitialApply()
    {
        Fixture fixture = Create();
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture));
        LiveUseUnchanged(fixture, before);
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.False);
        await Task.CompletedTask;
    }

    /// <summary>
    /// All CLI mutation operations reject a running configured game and retain their prior authority.
    /// </summary>
    /// <param name="operation">The production CLI operation.</param>
    /// <returns>The verification task.</returns>
    [TestCase("apply")]
    [TestCase("repair")]
    [TestCase("uninstall")]
    [TestCase("recover")]
    public async Task LiveUse_CliRejectsRunningGameWithoutLosingEvidence(string operation)
    {
        Fixture fixture = Create();
        if (operation == "recover")
        {
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
            {
                if (name == "engine-apply" && after)
                    throw new IOException("Interrupted publication");
            })));
        }
        else
        {
            await Apply(fixture);
        }
        fixture.Preparation.Setup(service => service.FetchTargetForInstalledReleaseAsync("release-three", "internal", null,
                fixture.Entry.Id, fixture.Target2.Identity, It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>()))
            .ReturnsAsync(fixture.Target3);
        fixture.Preparation.Setup(service => service.FetchRepairTargetAsync("release-three", "internal", null,
                fixture.Entry.Id, It.IsAny<CancellationToken>(), It.IsAny<Action<long, long>?>()))
            .ReturnsAsync(fixture.Target3);
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using var output = new StringWriter();
        await Assert.ThrowsAsync<IOException>(async () => await new FullSourceCommand().RunWithContextAsync(Context(fixture, output), new FullSourceSettings
        {
            Operation = operation, Release = "release-three", Channel = "internal", AssumeYes = true
        }));
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// A held managed bundle stops conversion before any archive or catalog mutation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_HeldManagedBundleRejectsDirectInitialApply()
    {
        Fixture fixture = Create();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using var editor = fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, BUNDLE), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<FilesInUseException>(async () => await Apply(fixture));
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// The shared GUI install-plan entry rejects used game files before changing a full-source generation.
    /// </summary>
    /// <param name="running">True to simulate the intended game, false to hold a managed bundle.</param>
    /// <returns>The verification task.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task LiveUse_SharedGuiPlanRejectsUsedFiles(bool running)
    {
        Fixture fixture = Create();
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(running);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using var output = new StringWriter();
        using var held = running ? null : fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, BUNDLE),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        var plan = new InstallPlan
        {
            FullSourceInstallationId = fixture.Entry.Id, FullSourceTarget = fixture.Target2, FullSourceStock = fixture.Stock
        };
        Type failure = running ? typeof(IOException) : typeof(FilesInUseException);
        await Assert.ThrowsAsync(failure, async () => await Context(fixture, output).InstallPlanService.ApplyToFolder(
            plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// Authentication delays cannot let a game start before initial catalog publication.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_GameStartsDuringPreparationRejectsBeforeCatalogInitialization()
    {
        Fixture fixture = Create();
        fixture.Preparation.Setup(service => service.VerifyTargetAsync(fixture.Target2, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
                return Task.CompletedTask;
            });
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture));
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// Editing the profile during verification cannot redirect an initial installation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_ProfileMovedDuringVerificationLeavesBothStockTreesIntact()
    {
        Fixture fixture = Create();
        string other = fixture.Fs.Path.Combine(fixture.Root, "other-game");
        foreach (var file in fixture.Stock.CurrentOwned.Files)
            Put(fixture.Fs, other, file.Path, fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, file.Path)));
        foreach (string directory in fixture.Stock.CurrentOwned.Directories)
            fixture.Fs.Directory.CreateDirectory(At(fixture.Fs, other, directory));
        fixture.Preparation.Setup(service => service.VerifyTargetAsync(fixture.Target2, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                fixture.Entry.ExePath = At(fixture.Fs, other, Ksp2Install.KSP2_EXE_NAME);
                return Task.CompletedTask;
            });
        Dictionary<string, string> before = LiveUseSnapshot(fixture);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => await Apply(fixture));

        Assert.That(failure!.Message, Does.Contain("configured installation folder changed"));
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// A stale selected directory cannot start installation, restoration or recovery.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_StaleSelectedRootRejectsBeforeStartingAnyOperation()
    {
        Fixture fixture = Create();
        string stale = fixture.Fs.Path.Combine(fixture.Root, "previous-game");
        var coordinator = fixture.Service();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await coordinator.ApplyAsync(
            fixture.Entry.Id, fixture.Target2, fixture.Stock, false, _ => { }, CancellationToken.None, stale));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await coordinator.UninstallAsync(
            fixture.Entry.Id, _ => { }, CancellationToken.None, stale));
        Assert.Throws<InvalidDataException>(() => coordinator.Reconcile(fixture.Entry.Id, stale));

        fixture.Preparation.Verify(service => service.VerifyTargetAsync(It.IsAny<FullSourcePreparedTarget>(),
            It.IsAny<CancellationToken>()), Times.Never);
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// A game appearing during long staging stops active-generation mutation and leaves recoverable preparation evidence.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_GameStartsDuringStagingPreservesActiveGenerationAndPendingEvidence()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        var before = LiveUseSnapshot(fixture).Where(file => file.Key.StartsWith(fixture.Install,
            StringComparison.Ordinal)).ToDictionary(file => file.Key, file => file.Value);
        var coordinator = fixture.Service((name, after) =>
        {
            if (name == "stage-create" && after)
                fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
        });
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, coordinator, fixture.Target3, initial: false));
        Assert.That(LiveUseSnapshot(fixture).Where(file => file.Key.StartsWith(fixture.Install,
            StringComparison.Ordinal)).ToDictionary(file => file.Key, file => file.Value), Is.EquivalentTo(before));
        Assert.Throws<IOException>(() => coordinator.GetPublishedIdentity(fixture.Entry.Id));
        Assert.That(fixture.Fs.File.Exists(fixture.Head), Is.True);
        Assert.That(fixture.Fs.File.Exists(fixture.Archive), Is.True);
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(false);
        FullSourceInstallationStatus recovered = coordinator.Reconcile(fixture.Entry.Id)!;
        Assert.That(recovered.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.That(recovered.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        Generation(fixture, recovered);
    }

    /// <summary>
    /// Direct uninstall preserves the complete archive and catalog when the archive is in use.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_DirectUninstallRejectsHeldOriginalArchive()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using var archiveUser = fixture.Fs.File.Open(fixture.Archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<FilesInUseException>(async () => await fixture.Service().UninstallAsync(
            fixture.Entry.Id, _ => { }, CancellationToken.None));
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// Startup cannot publish or roll back a pending installation while a managed file is used.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_StartupLeavesPendingRecoveryIntactWhileFilesAreUsed()
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && after)
                throw new IOException("Interrupted publication");
        })));
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        var coordinator = fixture.Service();
        var guard = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var installs = new Ksp2InstallService(fixture.Configuration, fixture.Fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), guard, coordinator);
        using (fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, BUNDLE), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            installs.TryLoadKsp2Install();
            Assert.Throws<FilesInUseException>(() => coordinator.Reconcile(fixture.Entry.Id));
            LiveUseUnchanged(fixture, before);
        }
        Generation(fixture, coordinator.Reconcile(fixture.Entry.Id)!);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Healthy identity and status reads still work while the game uses its installation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_HealthyReadOnlyStateWorksWhileGameRuns()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using var game = fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, BUNDLE), FileMode.Open, FileAccess.Read, FileShare.Read);
        var coordinator = fixture.Service();
        Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
        Assert.That(coordinator.Reconcile(fixture.Entry.Id)!.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.DoesNotThrow(() => coordinator.EnsureReady(fixture.Install));
        using var output = new StringWriter();
        Assert.That(await new FullSourceCommand().RunWithContextAsync(Context(fixture, output),
            new FullSourceSettings { Operation = "recover" }), Is.Zero);
        LiveUseUnchanged(fixture, before);
    }

    /// <summary>
    /// Recovery cannot roll back a partially changed generation until the intended game and file readers leave.
    /// </summary>
    /// <param name="running">True for process use, false for a held managed file.</param>
    /// <returns>The verification task.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task LiveUse_PartialMutationRecoveryRejectsUseThenRestoresPriorGeneration(bool running)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service(engineFault: (name, after) =>
        {
            if (after && name.StartsWith("install-", StringComparison.Ordinal))
                throw new IOException("Interrupted after the first installed file");
        }), fixture.Target3, initial: false));
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(running);
        using (var held = running ? null : fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, BUNDLE),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Type failure = running ? typeof(IOException) : typeof(FilesInUseException);
            Assert.Throws(failure, () => fixture.Service().Reconcile(fixture.Entry.Id));
            LiveUseUnchanged(fixture, before);
        }
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(false);
        FullSourceInstallationStatus recovered = fixture.Service().Reconcile(fixture.Entry.Id)!;
        Assert.That(recovered.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        Assert.That(recovered.Release, Is.EqualTo(fixture.Target2.Identity));
        Generation(fixture, recovered);
    }

    /// <summary>
    /// A launch reservation also excludes the first conversion before any full-source catalog exists.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_InitialConversionWaitsForConfiguredLaunchReservation()
    {
        Fixture fixture = Create();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using (fixture.Service().AcquireLaunchLease(fixture.Install))
        {
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture));
            LiveUseUnchanged(fixture, before);
        }
        Generation(fixture, await Apply(fixture));
    }

    /// <summary>
    /// A failed or timed-out Steam CLI handoff releases its reservation without changing installation evidence.
    /// </summary>
    /// <param name="startFails">True to reject the handoff, false to expire startup detection.</param>
    /// <returns>The verification task.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task LiveUse_CliSteamFailureAndTimeoutReleaseLaunchReservation(bool startFails)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        fixture.Entry.LaunchThroughSteam = true;
        if (startFails)
            fixture.GameProcesses.Setup(service => service.Start(It.IsAny<ProcessStartInfo>()))
                .Throws(new InvalidOperationException("Mock handoff rejected"));
        fixture.GameProcesses.Setup(service => service.WaitForStartAsync(fixture.Install,
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        using var output = new StringWriter();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        int result = await new LaunchCommand().RunWithContextAsync(Context(fixture, output), new LaunchSettings { NoBanner = true });
        Assert.That(result, Is.EqualTo(startFails ? ExitCode.LAUNCH_FAILED : ExitCode.SUCCESS));
        LiveUseUnchanged(fixture, before);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
    }

    /// <summary>
    /// Cancelling bounded Steam startup releases the lease without claiming that an external handoff was stopped.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_CliSteamCancellationReleasesLaunchReservation()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        fixture.Entry.LaunchThroughSteam = true;
        fixture.GameProcesses.Setup(service => service.WaitForStartAsync(fixture.Install,
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        using var output = new StringWriter();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        Assert.That(await new LaunchCommand().RunWithContextAsync(Context(fixture, output), new LaunchSettings { NoBanner = true }),
            Is.EqualTo(ExitCode.CANCELLED));
        LiveUseUnchanged(fixture, before);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
    }

    /// <summary>
    /// Complete full-source inspection includes held files in folders skipped by legacy patching without adopting them.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_HeldModsFileBlocksCompleteRootInspectionWithoutAdoptingUnknownFiles()
    {
        Fixture fixture = Create();
        Put(fixture.Fs, fixture.Install, "mods/unowned.txt", USER_BYTES);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using (fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, "mods/unowned.txt"),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<FilesInUseException>(async () => await Apply(fixture));
            LiveUseUnchanged(fixture, before);
        }
        FullSourceInstallationStatus installed = await Apply(fixture);
        Assert.That(installed.Generation.Files.Any(file => file.Path == "mods/unowned.txt"), Is.False);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/unowned.txt")), Is.EqualTo(USER_BYTES));
        Generation(fixture, installed);
    }

    /// <summary>
    /// Steam CLI launch retains the same-root lease through startup detection but does not serialize unrelated profiles.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_CliSteamHandoffExcludesSameRootInstallUntilDetection()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        Fixture other = Create(fixture.Fs, fixture.Fs.Path.Combine(fixture.Root, "unrelated"));
        fixture.Configuration.Config.Ksp2Installs.Add(other.Entry);
        other.Configuration.Config.Ksp2Installs.Add(fixture.Entry);
        fixture.Entry.LaunchThroughSteam = true;
        var appeared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GameProcesses.Setup(service => service.WaitForStartAsync(fixture.Install,
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(appeared.Task);
        using var output = new StringWriter();
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        Task<int> launch = new LaunchCommand().RunWithContextAsync(Context(fixture, output), new LaunchSettings { NoBanner = true });
        try
        {
            fixture.GameProcesses.Verify(service => service.Start(It.Is<ProcessStartInfo>(info =>
                info.FileName == "steam://rungameid/954850")), Times.Once);
            Assert.That(launch.IsCompleted, Is.False);
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, initial: false));
            LiveUseUnchanged(fixture, before);
            Generation(other, await Apply(other));
            fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
            appeared.SetResult(true);
            Assert.That(await launch, Is.Zero);
            Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, initial: false));
            fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(false);
            Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        }
        finally
        {
            appeared.TrySetResult(false);
            await launch;
        }
    }

    /// <summary>
    /// The real GUI native launch reserves the root across handoff and releases it before the game-session wait.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public async Task LiveUse_GuiDirectHandoffExcludesInstallAcrossStartup()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        var coordinator = fixture.Service();
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var installs = new Ksp2InstallService(fixture.Configuration, fixture.Fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), state, coordinator);
        var feeds = new Mock<IReleasesFeedService>();
        feeds.SetupGet(service => service.ReleasesFeed).Returns(new Dictionary<string, ManifestReleasesFeed>());
        feeds.SetupGet(service => service.Feeds).Returns([]);
        var handedOff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GameProcesses.Setup(service => service.Start(It.IsAny<ProcessStartInfo>()))
            .Callback(() => handedOff.TrySetResult());
        var appeared = new TaskCompletionSource<bool>();
        var exited = new TaskCompletionSource();
        fixture.GameProcesses.Setup(service => service.WaitForStartAsync(fixture.Install,
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(appeared.Task);
        fixture.GameProcesses.Setup(service => service.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(exited.Task);
        var home = new HomeTabViewModel(installs, fixture.Configuration, feeds.Object, Mock.Of<IInstallPlanService>(),
            Mock.Of<IUpdateService>(), fixture.Os, Mock.Of<IMessageBoxService>(), Mock.Of<IEnvironmentProvider>(),
            fixture.Fs, Mock.Of<ILogService>(), Mock.Of<IWineRuntimeService>(), fixture.GameProcesses.Object, state);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        Task launch = home.LaunchGame();
        try
        {
            await Task.WhenAny(handedOff.Task, launch).WaitAsync(TimeSpan.FromSeconds(10));
            if (!handedOff.Task.IsCompleted)
            {
                await launch;
                Assert.Fail("GUI launch completed before the process handoff.");
            }
            fixture.GameProcesses.Verify(service => service.Start(It.Is<ProcessStartInfo>(info =>
                info.FileName == fixture.Entry.ExePath)), Times.Once);
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, initial: false));
            LiveUseUnchanged(fixture, before);
            fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
            appeared.SetResult(true);
            Assert.That(launch.IsCompleted, Is.False);
            Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(fixture.Target2.Identity));
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, initial: false));
        }
        finally
        {
            appeared.TrySetResult(false);
            exited.TrySetResult();
            await launch.WaitAsync(TimeSpan.FromSeconds(10));
            await launch;
        }
    }

    /// <summary>
    /// A different running profile does not block mutation of the configured intended root.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LiveUse_UnrelatedRunningProfileDoesNotBlockApply()
    {
        Fixture fixture = Create();
        fixture.GameProcesses.Setup(service => service.IsRunning()).Returns(true);
        fixture.GameProcesses.Setup(service => service.IsRunning(@"C:\unrelated\game")).Returns(true);
        Generation(fixture, await Apply(fixture));
        fixture.GameProcesses.Verify(service => service.IsRunning(), Times.Never);
    }

    /// <summary>
    /// Settings' direct full-source uninstall reports use and retains installation and backup bytes.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public Task LiveUse_GuiSettingsUninstallRejectsRunningGame() => LiveUseSettingsRejectionAsync(true);

    /// <summary>
    /// Settings' direct full-source uninstall preserves the archive when a managed bundle is held.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public Task LiveUse_GuiSettingsUninstallRejectsHeldBundle() => LiveUseSettingsRejectionAsync(false);

    private async Task LiveUseSettingsRejectionAsync(bool running)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(running);
        var coordinator = fixture.Service();
        var state = new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), coordinator);
        var installs = new Ksp2InstallService(fixture.Configuration, fixture.Fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), state, coordinator);
        var feeds = new Mock<IReleasesFeedService>();
        feeds.SetupGet(service => service.ReleasesFeed).Returns(new Dictionary<string, ManifestReleasesFeed>());
        feeds.SetupGet(service => service.Feeds).Returns([]);
        var messages = new Mock<IMessageBoxService>();
        messages.Setup(service => service.ShowMessageBoxAsOwnedAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>())).ReturnsAsync(ButtonResult.Yes);
        using var planOutput = new StringWriter();
        IInstallPlanService plans = Context(fixture, planOutput).InstallPlanService;
        var home = new HomeTabViewModel(installs, fixture.Configuration, feeds.Object, plans,
            Mock.Of<IUpdateService>(), fixture.Os, messages.Object, Mock.Of<IEnvironmentProvider>(), fixture.Fs,
            Mock.Of<ILogService>(), Mock.Of<IWineRuntimeService>(), fixture.GameProcesses.Object, state);
        var settings = new SettingsTabViewModel(fixture.Fs,
            fixture.Configuration, installs, Mock.Of<ITabNavigatorService>(), home, Mock.Of<IAssemblyService>(),
            messages.Object, Mock.Of<IEnvironmentProvider>(), Mock.Of<ILogService>(), Mock.Of<IGameDataFolderService>(),
            Mock.Of<ISteamSessionService>(), Mock.Of<ISteamDialogService>(), fixture.Os,
            Mock.Of<IKsp2GameUninstallService>(), state, coordinator);
        Dictionary<string, string> before = LiveUseSnapshot(fixture);
        using var held = running ? null : fixture.Fs.File.Open(At(fixture.Fs, fixture.Install, BUNDLE),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        await settings.UninstallRedux();
        LiveUseUnchanged(fixture, before);
        Assert.That(messages.Invocations.Any(call => running
            ? call.Arguments.OfType<string>().Any(text => text.StartsWith("Couldn't uninstall Redux:", StringComparison.Ordinal))
            : Equals(call.Arguments[0], Ksp2Redux.Tools.Launcher.Localization.LocalizationService.Default["Home.FilesInUse.Title"])), Is.True);
    }
}
