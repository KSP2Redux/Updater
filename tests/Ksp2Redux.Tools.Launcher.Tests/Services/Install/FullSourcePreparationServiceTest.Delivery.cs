using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    /// <summary>
    /// Owned TLS delivery reaches actual native recipe preparation and both production interface transition routes.
    /// </summary>
    /// <returns>The verification task.</returns>
    [AvaloniaTest, Platform("Win")]
    public async Task Delivery_NativeLoopbackProbe_AuthenticatedComponentCliGuiAndOfflineUninstall()
    {
        string? frozen = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_PREPARATION_PROBE");
        if (string.IsNullOrWhiteSpace(frozen))
        {
            Assert.Ignore("Supply the retained frozen component publication through the maintained runner.");
            return;
        }
        var fs = new RealFileSystem();
        string workspace = fs.Path.GetDirectoryName(typeof(FullSourcePreparationServiceTest).Assembly.Location)!;
        while (!fs.Directory.Exists(At(fs, workspace, "Documentation/Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("The evidence checkout was not found.");
        }
        string evidence = At(fs, workspace, "Library/Omnipatching");
        frozen = fs.Path.GetFullPath(frozen);
        if (!frozen.StartsWith(evidence + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Frozen component inputs must be inside this checkout's ignored evidence.");
        string root = At(fs, evidence, $"launcher-preparation/delivery-{Guid.NewGuid():N}");
        fs.Directory.CreateDirectory(root);
        var timer = Stopwatch.StartNew();
        long allocation = GC.GetTotalAllocatedBytes();
        await using var server = new LoopbackReleaseServer();
        using var handler = server.Handler();
        try
        {
            StockBaselineManifest priorStock = StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(fs.File.ReadAllText(At(fs, frozen, "baseline.json"))));
            StockBaselineManifest priorTarget = StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(fs.File.ReadAllText(At(fs, frozen, "target.json"))));
            StockBaselineManifest stock = AddControls(priorStock, true);
            StockBaselineManifest target = AddControls(priorTarget);
            string stockRoot = At(fs, root, "stock");
            string targetRoot = At(fs, root, "target");
            foreach (var file in priorStock.Files)
            {
                string path = At(fs, stockRoot, DeliveryStockPath(file.Path));
                fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
                fs.File.Copy(At(fs, At(fs, frozen, "frozen-baseline"), file.Path), path);
            }
            CopyTree(fs, At(fs, frozen, "frozen-target"), targetRoot, priorTarget);
            foreach (string source in new[] { stockRoot, targetRoot })
            {
                Put(fs, source, Ksp2Install.KSP2_EXE_NAME, Encoding.ASCII.GetBytes("owned nonexecutable fixture control"));
                Put(fs, source, "KSP2_x64_Data/boot.config", Encoding.ASCII.GetBytes("fixture=delivery\r\n"));
            }
            string artifact = At(fs, root, "artifact");
            var pipeline = new ReleaseRecipePipeline(fs);
            ReleaseRecipePublication publication = pipeline.Publish(targetRoot, target,
                [new ReleaseRecipeSourceTree(stockRoot, stock, stock.Files.Select(file =>
                    new ReleaseRecipeSource(file.Path, BundleRecipeSourceKind.RawFile)).ToArray())], artifact);
            var assembly = new Mock<IAssemblyService>();
            assembly.Setup(service => service.GetName()).Returns(new AssemblyName("OwnedDelivery") { Version = new Version(1, 0) });
            var fixture = new Fixture(fs, root, stockRoot, stock, target, artifact, publication, configuration =>
                new ManifestReleasesFeedProviderService(assembly.Object, configuration, Mock.Of<ILogService>(), handler));
            const string userSave = "saves/delivery-sentinel.json";
            Put(fs, fixture.Install, userSave, Encoding.UTF8.GetBytes("{\"owner\":\"fixture-player\"}"));
            FeedInfo configured = fixture.Config.Feeds.Single();
            configured.Token = LoopbackReleaseServer.TEST_TOKEN;
            fixture.Config.PatchDownloadSource = PatchDownloadSource.GitHub;
            fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource! with
            {
                GitHubArtifactUrl = "https://github.com/fixture/release-authority/releases/download/r1/release.json"
            } };
            string[] names = fs.Directory.GetFiles(artifact).Select(fs.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
            int corruptions = 0;
            bool offline = false;
            server.Respond = async (request, stream, ct) =>
            {
                int status = 200;
                byte[] body = [];
                string headers = "";
                if (offline)
                    status = 503;
                else if (request.Host == "api.github.com" && !request.Authorized)
                    status = 401;
                else if (request.Path.StartsWith("/repos/fixture/release-authority/contents/", StringComparison.Ordinal))
                    body = JsonSerializer.SerializeToUtf8Bytes(fixture.Feed() with { Channel = fixture.Patch.FullSource!.Channel });
                else if (request.Path == "/repos/fixture/release-authority/releases/tags/r1")
                    body = JsonSerializer.SerializeToUtf8Bytes(new { assets = names.Select((name, index) => new
                        { name, url = $"https://api.github.com/repos/fixture/release-authority/releases/assets/{index + 1}" }) });
                else if (request.Host == "api.github.com" && request.Path.StartsWith("/repos/fixture/release-authority/releases/assets/", StringComparison.Ordinal))
                {
                    status = 302;
                    headers = $"Location: https://objects.fixture.test/build/{names[int.Parse(request.Path.Split('/').Last()) - 1]}\r\n";
                }
                else if (request.Host == "objects.fixture.test")
                {
                    Assert.That(request.Authorized, Is.False);
                    string name = request.Path.Split('/').Last();
                    body = fs.File.ReadAllBytes(At(fs, artifact, name));
                    if (name.StartsWith("pack-", StringComparison.Ordinal) && Interlocked.Increment(ref corruptions) == 1)
                        body[^1] ^= 1;
                }
                else
                    status = 404;
                await LoopbackReleaseServer.ReplyAsync(stream, body, status, ct, headers);
            };
            configured.Token = "rejected-owned-test-token";
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await fixture.Preparation.FetchTargetAsync(RELEASE_ID, CHANNEL, configured.SnapshotAuthority(), CancellationToken.None));
            AssertTree(fs, fixture.Install, stock);
            configured.Token = LoopbackReleaseServer.TEST_TOKEN;
            IServiceProvider services = Services(fixture);
            using var output = new StringWriter();
            var context = new CliContext(services, new CliOutput(output, isJson: true));
            Assert.That(await new InstallCommand().RunWithContextAsync(context, new InstallSettings
            {
                Version = VERSION, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
            }), Is.Zero, output.ToString());
            AssertTreeStreaming(fs, fixture.Install, target);
            string archive = At(fs, fixture.Install, "uninstall.zip");
            string archiveHash = HashFile(fs, archive);
            fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource! with { Channel = "stable", ReleaseId = "owned-stable" } };
            Assert.That(await new UpdateCommand().RunWithContextAsync(context, new UpdateSettings
            {
                Channel = "stable", Install = fixture.Entry.Id.ToString()
            }), Is.Zero, output.ToString());
            Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!.Channel, Is.EqualTo("stable"));
            fixture.Patch = fixture.Patch with { FullSource = fixture.Patch.FullSource! with { Channel = CHANNEL, ReleaseId = "owned-gui-beta" } };
            var installs = services.GetRequiredService<IKsp2InstallService>();
            installs.TryLoadKsp2Install();
            var feed = new ManifestReleasesFeed(fixture.Provider, Mock.Of<ILogService>(), fixture.Storage, configured);
            Assert.That(await feed.UpdateManifest(), Is.True);
            var feeds = services.GetRequiredService<IReleasesFeedService>();
            feeds.Add(feed);
            var home = new HomeTabViewModel(installs, fixture.Configuration.Object, feeds, services.GetRequiredService<IInstallPlanService>(),
                Mock.Of<IUpdateService>(), fixture.Os.Object, Mock.Of<IMessageBoxService>(), services.GetRequiredService<IEnvironmentProvider>(),
                fs, Mock.Of<ILogService>(), Mock.Of<IWineRuntimeService>(), services.GetRequiredService<IGameProcessService>(),
                services.GetRequiredService<IInstallationStateService>());
            home.SelectedVersion = new GameVersionViewModel(feed.GetAllVersions().Single());
            home.RefreshMainButtonState();
            Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Update));
            await home.UpdateReduxCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!.ReleaseId, Is.EqualTo("owned-gui-beta"), home.InstallLogText);
            AssertTreeStreaming(fs, fixture.Install, target);

            var media = target.Files.First(file => file.Path.EndsWith(".wem", StringComparison.Ordinal));
            fs.File.WriteAllBytes(At(fs, fixture.Install, media.Path), [0, 1, 2]);
            fs.File.Delete(At(fs, fixture.Install, Ksp2Install.KSP2_EXE_NAME));
            string downloads = At(fs, fixture.Storage, "full-source-preparation-v1/downloads");
            string retainedDownloads = At(fs, root, "downloads-before-repair");
            Assert.That(downloads, Does.StartWith(root + fs.Path.DirectorySeparatorChar));
            Assert.That(retainedDownloads, Does.StartWith(root + fs.Path.DirectorySeparatorChar));
            fs.Directory.Move(downloads, retainedDownloads);
            home.SelectedVersion = new GameVersionViewModel(feed.GetAllVersions().Single());
            home.RefreshMainButtonState();
            Assert.That(home.IsRepairAvailable, Is.True);
            Assert.That(home.MainButtonEnabled, Is.False);
            int requestsBeforeRepair = server.Requests.Count;
            await home.RepairReduxCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            AssertTreeStreaming(fs, fixture.Install, target);
            Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!.ReleaseId, Is.EqualTo("owned-gui-beta"));
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveHash));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userSave)), Is.EqualTo("{\"owner\":\"fixture-player\"}"));
            Assert.That(home.DownloadProgressMb, Is.EqualTo(home.DownloadProgressTotalMb).And.Positive);
            int repairAssetRequests = server.Requests.Skip(requestsBeforeRepair).Count(request => request.Host == "objects.fixture.test");
            Assert.That(repairAssetRequests, Is.Positive);

            offline = true;
            Assert.That(await new UninstallCommand().RunWithContextAsync(context, new UninstallSettings
                { AssumeYes = true, Install = fixture.Entry.Id.ToString() }), Is.Zero, output.ToString());
            AssertTreeStreaming(fs, fixture.Install, stock);
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveHash));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
            Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, userSave)), Is.EqualTo("{\"owner\":\"fixture-player\"}"));
            AssertTreeStreaming(fs, At(fs, frozen, "frozen-baseline"), priorStock);
            AssertTreeStreaming(fs, At(fs, frozen, "frozen-target"), priorTarget);
            Assert.That(corruptions, Is.GreaterThanOrEqualTo(2));
            Assert.That(server.Requests.Where(request => request.Host != "api.github.com").All(request => !request.Authorized), Is.True);
            ReleaseRecipeManifest inspected = pipeline.Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
            for (int index = 0; index < inspected.Packs.Count; index++)
            {
                Assert.That(HashFile(fs, At(fs, artifact, $"pack-{index:D6}.bin")), Is.EqualTo(inspected.Packs[index].Sha256));
            }
            using var process = Process.GetCurrentProcess();
            string report = At(fs, root, "result.json");
            fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Success = true, RealTlsLoopback = true, NativeWindowsFilesystem = true,
                ExplicitTestCertificateAndCredential = true, ExternalPublisherAuthentication = false,
                FullRebuiltGame = false, NativeLinuxMacOrWine = false, GuiVisualVerification = false,
                CliInstallSameVersionChannelTransition = true, GuiHomeCommandChannelTransition = true,
                GuiHomeCommandRepair = true, RepairedMissingExecutable = true, RepairedDamagedWem = true,
                RepairLoopbackAssetRequests = repairAssetRequests, RepairReportedMiB = home.DownloadProgressMb,
                UserSavePreserved = true,
                RejectedCredentialBeforeMutation = true, CorruptPackRetriedAndVerified = true,
                OfflineUninstall = true, CompleteFixtureStockArchiveIncludesStreamingAssets = true,
                ArchiveSha256 = archiveHash, UnknownUserBytesPreserved = true, FrozenInputsUnchanged = true,
                FrozenInput = frozen, StockBytes = stock.TotalBytes, TargetBytes = target.TotalBytes,
                publication.ArtifactSha256, publication.TargetManifestSha256, publication.StoredPackBytes,
                ExactRestoredFiles = stock.Files, ExactTargetFiles = target.Files, Installation = fixture.Install,
                Artifact = artifact, StockRoot = stockRoot, TargetRoot = targetRoot,
                Requests = server.Requests.ToArray(), ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                CumulativeManagedAllocatedBytes = GC.GetTotalAllocatedBytes() - allocation,
                ProcessLifetimePeakWorkingSetBytes = process.PeakWorkingSet64, PagingAllowed = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(report, "Owned TLS component delivery through production preparation and interface routes.");
            TestContext.Progress.WriteLine($"Owned delivery evidence: {report}");
        }
        catch (Exception failure)
        {
            fs.File.WriteAllBytes(At(fs, root, "failure.json"), JsonSerializer.SerializeToUtf8Bytes(new
                { Type = failure.GetType().FullName, failure.Message, Requests = server.Requests.ToArray() }));
            throw;
        }
    }

    private static string DeliveryStockPath(string path) => path switch
    {
        "stock/servicebay.bundle" => "KSP2_x64_Data/StreamingAssets/aa/StandaloneWindows64/servicebay-source.bundle",
        "stock/1000486374.wem" => "KSP2_x64_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/1000486374.wem",
        _ => throw new InvalidDataException("The retained component baseline shape changed. Review its exact mapping before delivery.")
    };

    private static StockBaselineManifest AddControls(StockBaselineManifest manifest, bool remapStock = false)
    {
        byte[] exe = Encoding.ASCII.GetBytes("owned nonexecutable fixture control");
        byte[] boot = Encoding.ASCII.GetBytes("fixture=delivery\r\n");
        return new StockBaselineManifest(manifest.BaselineIdentity, "Actual component fixture with synthetic executable and boot controls",
            manifest.Files.Select(file => remapStock ? new StockBaselineFile(DeliveryStockPath(file.Path), file.Length, file.Sha256) : file)
                .Concat(new[] { new StockBaselineFile(Ksp2Install.KSP2_EXE_NAME, exe.Length, Digest(exe)),
                new StockBaselineFile("KSP2_x64_Data/boot.config", boot.Length, Digest(boot)) }), remapStock ? [] : manifest.Directories);
    }
}
