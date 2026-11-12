using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    private static FeedInfo AddUnavailableFeed(Fixture fixture)
    {
        var unrelated = new FeedInfo { Repository = "https://github.com/fixture/unavailable", Filename = "stable.json" };
        fixture.Config.Feeds.Insert(0, unrelated);
        fixture.Transport.Setup(service => service.GetTrustedManifestAsync(unrelated, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unrelated channel is unavailable."));
        fixture.Transport.Setup(service => service.GetManifest(unrelated))
            .ThrowsAsync(new HttpRequestException("Unrelated channel is unavailable."));
        return unrelated;
    }

    private static async Task<InstallPlan> SelectedPlan(Fixture fixture, bool repair = false)
    {
        var feed = new ManifestReleasesFeed(fixture.Fs, fixture.Transport.Object, Mock.Of<ILogService>(),
            fixture.Storage, fixture.Config.Feeds.Single());
        Assert.That(await feed.UpdateManifest(), Is.True);
        var selected = feed.GetAllVersions().Single();
        if (repair)
            return feed.GetFullSourcePlan(selected, repair: true)!;
        return feed.GetPatchListToVersion(new GameVersion { VersionNumber = new Version(0, 2, 2, 0), BuildNumber = "32914" },
            selected);
    }

    /// <summary>
    /// An unavailable unrelated channel cannot block execution or fresh verification of the selected authority.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_SelectedReleaseWithUnrelatedOfflineFeed_InstallsAndVerifies()
    {
        Fixture fixture = Create();
        InstallPlan plan = await SelectedPlan(fixture);
        AddUnavailableFeed(fixture);
        await Services(fixture).GetRequiredService<IInstallPlanService>().ApplyToFolder(plan, fixture.Install,
            _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }

    /// <summary>
    /// Removing a list-time authority cannot redirect its selection to another configured feed with matching claims.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_SelectedFeedRemoved_CannotRebindToAnotherPublisher()
    {
        Fixture fixture = Create();
        InstallPlan plan = await SelectedPlan(fixture);
        fixture.Config.Feeds.Clear();
        fixture.Config.Feeds.Add(new FeedInfo { Repository = "https://github.com/fixture/other-publisher", Filename = "beta.json" });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Services(fixture).GetRequiredService<IInstallPlanService>().ApplyToFolder(plan, fixture.Install,
                _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
        Assert.That(fixture.Requests, Is.Empty);
    }

    /// <summary>
    /// Permission changes under the same release ID and version invalidate a list-time selection.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_SelectedPermissionChanged_IsRejected()
    {
        Fixture fixture = Create();
        InstallPlan plan = await SelectedPlan(fixture);
        fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource! with { AllowsDerivedBootConfig = true } };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Services(fixture).GetRequiredService<IInstallPlanService>().ApplyToFolder(plan, fixture.Install,
                _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));
        Assert.That(fixture.Requests, Is.Empty, "Changed release selections must stop before artifact requests.");
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Republishing different target bytes under the same selector and version cannot change a list-time target contract.
    /// </summary>
    /// <param name="repair">Null for initial install, false for a managed update, or true for repair.</param>
    /// <returns>The verification task.</returns>
    [TestCase(null)]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Review_SelectedTargetDigestChanged_IsRejected(bool? repair)
    {
        Fixture fixture = Create();
        var installPlans = Services(fixture).GetRequiredService<IInstallPlanService>();
        if (repair is not null)
        {
            var initial = await SelectedPlan(fixture);
            await installPlans.ApplyToFolder(initial, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
            fixture.Requests.Clear();
        }
        InstallPlan plan = await SelectedPlan(fixture, repair == true);
        string changedRoot = At(fixture.Fs, fixture.Root, "changed-target");
        CopyTree(fixture.Fs, At(fixture.Fs, fixture.Root, "frozen-target"), changedRoot, fixture.Target);
        byte[] media = [1, 2, 3, 4, 5];
        Put(fixture.Fs, changedRoot, WEM, media);
        var changed = new StockBaselineManifest(fixture.Target.BaselineIdentity,
            fixture.Target.Provenance, fixture.Target.Files.Select(file => file.Path == WEM
                ? new StockBaselineFile(WEM, media.Length, Digest(media)) : file), fixture.Target.Directories);
        string artifact = At(fixture.Fs, fixture.Root, "changed-artifact");
        var pipeline = new ReleaseRecipePipeline(fixture.Fs, _ => { });
        var publication = pipeline.Publish(changedRoot, changed, [new ReleaseRecipeSourceTree(
            fixture.StockRoot, fixture.Stock, [])], artifact);
        fixture.Patch = pipeline.CreateFeedEntry(artifact, publication, RELEASE_ID, CHANNEL, VERSION,
            "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
            [new FullSourceBaselineDefinition(fixture.Stock.ToJson(), [])], false, DateTime.UtcNow);
        fixture.ResponseOverride = (url, _, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(fixture.Fs.File.OpenRead(At(fixture.Fs, artifact, new Uri(url).Segments.Last())))
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installPlans.ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));
        Assert.That(fixture.Requests, Is.Empty, "Changed release selections must stop before artifact requests.");
        AssertTree(fixture.Fs, fixture.Install, repair is null ? fixture.Stock : fixture.Target);
    }

    /// <summary>
    /// A prepared selection keeps fresh verification bound to its publisher even when another feed supplies matching claims.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_SelectedAuthorityOffline_VerificationCannotUseOtherFeed()
    {
        Fixture fixture = Create();
        var target = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        var stock = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, target, CancellationToken.None);
        FeedInfo selected = fixture.Config.Feeds.Single();
        fixture.Config.Feeds.Add(new FeedInfo { Repository = "https://github.com/fixture/other-publisher", Filename = "beta.json" });
        fixture.Transport.Setup(service => service.GetTrustedManifestAsync(selected, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Selected authority is unavailable."));
        fixture.Transport.Invocations.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Preparation.VerifyTargetAsync(target, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.Preparation.VerifyStockAsync(stock, CancellationToken.None));
        fixture.Transport.Verify(service => service.GetTrustedManifestAsync(fixture.Config.Feeds[1], It.IsAny<CancellationToken>()), Times.Never);
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Selector-only CLI acquisition tolerates an unrelated unavailable channel and binds later verification to the successful authority.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_ExplicitCliApplyWithUnrelatedOfflineFeed_Succeeds()
    {
        Fixture fixture = Create();
        AddUnavailableFeed(fixture);
        using var output = new StringWriter();
        var context = new CliContext(Services(fixture), new CliOutput(output, isJson: true));
        Assert.That(await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "apply", Release = RELEASE_ID, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }

    /// <summary>
    /// Refuse an invalid initial operation before requesting patch artifacts.
    /// </summary>
    /// <param name="explicitCli">Use the explicit full-source CLI entry point.</param>
    /// <param name="repair">Request repair for an unmanaged installation.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task InitialInstallVerifiesSelectedStockBeforeDownloading(bool explicitCli, bool repair)
    {
        Fixture fixture = Create();
        if (!repair)
        {
            string other = At(fixture.Fs, fixture.Root, "verified-alternate");
            CopyTree(fixture.Fs, fixture.StockRoot, other, fixture.Stock);
            fixture.Config.Ksp2Installs.Add(new Ksp2InstallEntry
            {
                ExePath = At(fixture.Fs, other, Ksp2Install.KSP2_EXE_NAME), Name = "Verified alternate fixture"
            });
            Put(fixture.Fs, fixture.Install, WEM, [3, 2, 1]);
        }

        using var output = new StringWriter();
        var services = Services(fixture);
        InvalidDataException? failure;
        if (explicitCli)
        {
            var context = new CliContext(services, new CliOutput(output, isJson: true));
            failure = await Assert.ThrowsAsync<InvalidDataException>(async () => await new FullSourceCommand().RunWithContextAsync(
                context, new FullSourceSettings
                {
                    Operation = repair ? "repair" : "apply", Release = RELEASE_ID, Channel = CHANNEL,
                    Install = fixture.Entry.Id.ToString()
                }));
        }
        else
        {
            var plan = new InstallPlan
            {
                FullSourceReleaseId = RELEASE_ID, FullSourceVersion = VERSION, FullSourceChannel = CHANNEL,
                FullSourceFeed = fixture.Config.Feeds.Single().SnapshotAuthority(), IsFullSourceRepair = repair
            };
            failure = await Assert.ThrowsAsync<InvalidDataException>(async () => await services.GetRequiredService<IInstallPlanService>()
                .ApplyToFolder(plan, fixture.Install, _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));
        }

        Assert.That(failure!.Message, Does.Contain(repair ? "requires independently verified stock and ownership" : "complete trusted original stock"));
        Assert.That(fixture.Requests, Is.Empty, "Initial stock verification must precede any artifact request.");
        Assert.That(fixture.Coordinator().HasCatalog(fixture.Install), Is.False);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, fixture.Install, "uninstall.zip")), Is.False);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
    }

    /// <summary>
    /// Ordinary CLI selection continues through an unrelated failed channel into authenticated preparation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Review_CliInstallWithUnrelatedOfflineFeed_Succeeds()
    {
        Fixture fixture = Create();
        AddUnavailableFeed(fixture);
        using var output = new StringWriter();
        var context = new CliContext(Services(fixture), new CliOutput(output, isJson: true));
        Assert.That(await new InstallCommand().RunWithContextAsync(context, new InstallSettings
        {
            Version = VERSION, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }

    private static IServiceProvider Services(Fixture fixture)
    {
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Configuration.Object);
        services.AddSingleton(fixture.Fs);
        services.AddSingleton<IZipFileService>(new ZipFileService(fixture.Fs));
        services.AddSingleton(fixture.Space.Object);
        services.AddSingleton(fixture.Os.Object);
        services.AddSingleton<IFullSourcePreparationService>(fixture.Preparation);
        services.AddSingleton<IFullSourceInstallationService>(fixture.Coordinator());
        services.AddSingleton<IInstallationStateService, InstallationStateService>();
        services.AddSingleton<IKsp2InstallService, Ksp2InstallService>();
        services.AddSingleton<IInstallPlanService, InstallPlanService>();
        services.AddSingleton<ICacheService>(new Mock<ICacheService>(MockBehavior.Strict).Object);
        services.AddSingleton<IPatchDownloadService>(fixture.Downloads);
        services.AddSingleton<IGameProcessService>(Mock.Of<IGameProcessService>());
        var modules = new Mock<IModuleDefinitionService>();
        modules.Setup(service => service.ReadModule(It.IsAny<string>())).Returns(() =>
            TestHelpers.GenerateMockVersionID(("VERSION_TEXT", "0.2.2.0.32914"), ("DEBUG_INFO", "fixture-stock")).module);
        services.AddSingleton<IModuleDefinitionService>(fixture.FullPlayer is null ? modules.Object : new ModuleDefinitionService());
        services.AddSingleton(Mock.Of<ILogService>());
        services.AddSingleton(Mock.Of<IEnvironmentProvider>());
        services.AddSingleton(Mock.Of<IEnvironmentVariableProvider>());
        services.AddSingleton(Mock.Of<IAssemblyService>());
        services.AddSingleton<IReleasesFeedService, ReleasesFeedService>();
        services.AddSingleton(Mock.Of<IKsp2DetectorService>());
        services.AddSingleton(Mock.Of<IGameDataFolderService>());
        services.AddSingleton<IKsp2GameUninstallService, Ksp2GameUninstallService>();
        services.AddSingleton(Mock.Of<IWineRuntimeService>());
        services.AddSingleton(Mock.Of<ISteamSessionService>());
        services.AddSingleton(fixture.Steam.Object);
        services.AddSingleton(Mock.Of<INewsProviderService>());
        services.AddSingleton(fixture.Provider);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Ordinary feed-selected CLI install and update consume real preparation instead of treating recipes as legacy patches.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task CliInstallUpdateAndExplicitRepair_UseRealPreparationAndCoordinator()
    {
        Fixture fixture = Create();
        using var output = new StringWriter();
        var context = new CliContext(Services(fixture), new CliOutput(output, isJson: true));
        Assert.That(await new InstallCommand().RunWithContextAsync(context, new InstallSettings
        {
            Version = VERSION, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        var descriptor = fixture.Patch.FullSource! with { ReleaseId = "fixture-release-three", Version = "2.0.0.1.201" };
        fixture.Patch = fixture.Patch with { Version = descriptor.Version, FullSource = descriptor };
        Assert.That(await new UpdateCommand().RunWithContextAsync(context, new UpdateSettings
        {
            Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        Assert.That(fixture.Entry.LastInstalledVersion!.BuildNumber, Is.EqualTo("201"));
        Put(fixture.Fs, fixture.Install, WEM, [7, 8, 9]);
        Assert.That(await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "repair", Release = descriptor.ReleaseId, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        fixture.Offline = true;
        Assert.That(await new UninstallCommand().RunWithContextAsync(context, new UninstallSettings
        {
            AssumeYes = true, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// A same-version release on another channel or with another release identity still publishes its generation.
    /// </summary>
    /// <param name="channel">The newly selected release channel.</param>
    /// <returns>The verification task.</returns>
    [TestCase("stable"), TestCase(CHANNEL)]
    public async Task Delivery_CliSameVersionTransition_PublishesSelectedIdentity(string channel)
    {
        Fixture fixture = Create();
        using var output = new StringWriter();
        var context = new CliContext(Services(fixture), new CliOutput(output, isJson: true));
        Assert.That(await new InstallCommand().RunWithContextAsync(context, new InstallSettings
        {
            Version = VERSION, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        byte[] archive = fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, "uninstall.zip"));
        fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource! with
            { Channel = channel, ReleaseId = "fixture-same-version-transition" } };
        fixture.Transport.Setup(service => service.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(() =>
            fixture.Feed() with { Channel = channel });
        fixture.Transport.Setup(service => service.GetTrustedManifestAsync(It.IsAny<FeedInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TrustedReleaseManifest(fixture.Feed() with { Channel = channel }, AUTHORITY));
        Assert.That(await new UpdateCommand().RunWithContextAsync(context, new UpdateSettings
        {
            Channel = channel, Install = fixture.Entry.Id.ToString()
        }), Is.Zero, output.ToString());
        FullSourceReleaseIdentity identity = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!;
        Assert.That(identity.ReleaseId, Is.EqualTo("fixture-same-version-transition"), output.ToString());
        Assert.That(identity.Channel, Is.EqualTo(channel));
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
        Assert.That(fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Install, "uninstall.zip")), Is.EqualTo(archive));
    }

    /// <summary>
    /// Home offers Update rather than Launch for another full-source identity sharing the installed version.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public async Task Delivery_GuiSameVersionTransition_OffersAndPublishesUpdate()
    {
        Fixture fixture = Create();
        var prepared = await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, CancellationToken.None);
        await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, prepared,
            await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, prepared, CancellationToken.None), false, _ => { }, CancellationToken.None);
        fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource! with { ReleaseId = "fixture-same-version-transition" } };
        IServiceProvider services = Services(fixture);
        var installs = services.GetRequiredService<IKsp2InstallService>();
        installs.TryLoadKsp2Install();
        var feed = new ManifestReleasesFeed(fixture.Fs, fixture.Transport.Object, Mock.Of<ILogService>(), fixture.Storage, fixture.Config.Feeds[0]);
        await feed.UpdateManifest();
        services.GetRequiredService<IReleasesFeedService>().AddOrSet(CHANNEL, feed);
        var home = new HomeTabViewModel(installs, fixture.Configuration.Object, services.GetRequiredService<IReleasesFeedService>(),
            services.GetRequiredService<IInstallPlanService>(), Mock.Of<IUpdateService>(), fixture.Os.Object,
            Mock.Of<IMessageBoxService>(), services.GetRequiredService<IEnvironmentProvider>(), fixture.Fs,
            Mock.Of<ILogService>(), Mock.Of<IWineRuntimeService>(), services.GetRequiredService<IGameProcessService>(),
            services.GetRequiredService<IInstallationStateService>());
        home.SelectedVersion = new GameVersionViewModel(feed.GetAllVersions().Single());
        home.RefreshMainButtonState();
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Update));
        Assert.That(home.MainButtonEnabled, Is.True);
        await home.InstallRedux();
        Dispatcher.UIThread.RunJobs();
        Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!.ReleaseId,
            Is.EqualTo("fixture-same-version-transition"), home.InstallLogText);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }

    /// <summary>
    /// The actual Home install action uses recipe-aware feed planning and the production generation coordinator.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest]
    public async Task GuiHomeInstall_ConsumesFeedDescriptorThroughRealPreparation()
    {
        Fixture fixture = Create();
        IServiceProvider services = Services(fixture);
        var installs = services.GetRequiredService<IKsp2InstallService>();
        installs.TryLoadKsp2Install();
        var feed = new ManifestReleasesFeed(fixture.Fs, fixture.Transport.Object, Mock.Of<ILogService>(),
            At(fixture.Fs, fixture.Storage, "legacy-downloads"), fixture.Config.Feeds[0]);
        await feed.UpdateManifest();
        var feeds = services.GetRequiredService<IReleasesFeedService>();
        feeds.AddOrSet(CHANNEL, feed);
        var home = new HomeTabViewModel(installs, fixture.Configuration.Object, feeds,
            services.GetRequiredService<IInstallPlanService>(), Mock.Of<IUpdateService>(), fixture.Os.Object,
            Mock.Of<IMessageBoxService>(), services.GetRequiredService<IEnvironmentProvider>(), fixture.Fs,
            Mock.Of<ILogService>(), Mock.Of<IWineRuntimeService>(), services.GetRequiredService<IGameProcessService>(),
            services.GetRequiredService<IInstallationStateService>());
        home.SelectedVersion = new GameVersionViewModel(feed.GetAllVersions().Single());
        await home.InstallRedux();
        Dispatcher.UIThread.RunJobs();
        Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!.Version, Is.EqualTo(VERSION), home.InstallLogText);
        AssertTree(fixture.Fs, fixture.Install, fixture.Target);
    }

    /// <summary>
    /// A list-time selected version cannot silently become a different version under the same release selector.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task FeedSelectionVersionChangedDuringPreparation_IsRejected()
    {
        Fixture fixture = Create();
        var service = Services(fixture).GetRequiredService<IInstallPlanService>();
        var plan = new InstallPlan { FullSourceReleaseId = RELEASE_ID, FullSourceChannel = CHANNEL, FullSourceVersion = "2.0.0.1.199" };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ApplyToFolder(plan, fixture.Install,
            _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None));
        Assert.That(fixture.Requests, Is.Empty, "Changed release selections must stop before artifact requests.");
        AssertTree(fixture.Fs, fixture.Install, fixture.Stock);
    }

    /// <summary>
    /// Schema-three full-source metadata cannot be consumed as a schema-one or schema-two legacy patch.
    /// </summary>
    /// <param name="schema">The older feed schema.</param>
    [TestCase(1), TestCase(2)]
    public void FullSourceDescriptorInLegacySchema_IsRejected(int schema)
    {
        Fixture fixture = Create();
        var manifest = fixture.Feed();
        manifest.SchemaVersion = schema;
        Assert.Throws<InvalidDataException>(() => ReleaseManifestValidator.Validate(manifest));
    }
}
