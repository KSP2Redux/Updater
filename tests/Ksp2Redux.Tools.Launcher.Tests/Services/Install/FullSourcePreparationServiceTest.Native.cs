using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Models;
using Testably.Abstractions;
using Ksp2Redux.Tools.Tests;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    /// <summary>
    /// Adopt the exact retained r9 install through the ordinary CLI with its verified co-located stock backup.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativeLegacyR9Probe_AdoptsExactPublishedOwnershipWithoutDownloading()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_LEGACY_R9");
        if (string.IsNullOrWhiteSpace(binding))
        {
            Assert.Ignore("Set KSP2REDUX_NATIVE_LEGACY_R9 to the retained RX8 fixture after native r9 update and stock backup verification.");
            return;
        }
        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, "RX8");
        string install = At(fs, root, "game");
        string output = At(fs, root, "launcher");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(root));
        Assert.That(fs.Directory.Exists(output) || fs.File.Exists(output), Is.False, "Use the initial legacy fixture before any catalog exists.");
        Assert.That(fs.File.Exists(At(fs, install, "mods/user.txt")), Is.False, "The new test sentinel must be absent.");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string delta = At(fs, batch, "r9-integrated-runtime-r1/delta");
        string evidence = At(fs, batch, "native-r8-r9-in-place-r1");
        using var backup = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, evidence, "stock-backup-result.json")));
        using var accepted = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, evidence, "result.json")));
        string archive = At(fs, install, "uninstall.zip");
        Assert.That(backup.RootElement.GetProperty("destination").GetString(), Is.EqualTo(archive));
        Assert.That(backup.RootElement.GetProperty("verifiedBeforePublication").GetBoolean(), Is.True);
        Assert.That(accepted.RootElement.GetProperty("retryCommitted").GetBoolean(), Is.True);
        Assert.That(accepted.RootElement.GetProperty("game").GetString(), Is.EqualTo(install));
        var stock = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json")));
        var target = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, delta, "r9-target-input.json")));
        Assert.That(Digest(target.ToJson()), Is.EqualTo(accepted.RootElement.GetProperty("targetSha").GetString()));
        string archiveSha = backup.RootElement.GetProperty("archiveSha256").GetString()!;
        Assert.That(fs.FileInfo.New(archive).Length, Is.EqualTo(backup.RootElement.GetProperty("archiveBytes").GetInt64()));
        var patch = new ReleasePatch
        {
            Version = VERSION, Requires = new PatchRequirement(), ReleasedAt = DateTime.UtcNow,
            Size = 1, ChecksumSha256 = Digest([0]),
            FullSource = new FullSourceReleaseDescriptor(RELEASE_ID, CHANNEL, VERSION, target.ToJson(), Digest(target.ToJson()),
                "https://fixture.test/unavailable-stock/release.json", "https://fixture-backup.test/unavailable-stock/release.json",
                [new FullSourceBaselineDefinition(stock.ToJson(), [], [target.ToJson()])], false, 1)
        };
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = CHANNEL, GeneratedAt = DateTime.UtcNow, Patches = [patch] };
        var fixture = new Fixture(fs, root, At(fs, workspace, "RTX1/game"), stock, target, At(fs, delta, "artifact"), null!,
            initializeStock: false, suppliedFeed: feed, suppliedPatch: patch);
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string path) =>
            fs.DriveInfo.New(fs.Path.GetPathRoot(path)!).AvailableFreeSpace);
        fixture.ResponseOverride = (_, _, _) => throw new InvalidOperationException("Exact adoption must not fetch the unavailable stock artifact.");
        using var log = new StringWriter();
        var context = new CliContext(Services(fixture), new CliOutput(log, isJson: true));
        int code = await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
        {
            Operation = "apply", Release = RELEASE_ID, Channel = CHANNEL, Install = fixture.Entry.Id.ToString()
        });
        fs.File.WriteAllText(At(fs, output, "native-legacy-adoption.cli.jsonl"), log.ToString());
        Assert.That(code, Is.Zero, log.ToString());
        Assert.That(fixture.Requests, Is.Empty);
        Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)!.ManifestSha256, Is.EqualTo(Digest(target.ToJson())));
        string stage = fs.Directory.EnumerateDirectories(At(fs, output, "full-source-preparation-v1/stages")).Single();
        Assert.That(fs.Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories), Is.Empty);
        AssertTreeStreaming(fs, install, target);
        Assert.That(HashFile(fs, archive), Is.EqualTo(archiveSha));
        Assert.That(fs.File.ReadAllText(At(fs, install, "mods/sparse-update-user-mod.bin")), Is.EqualTo("independent Windows update mod sentinel"));
        Assert.That(fs.File.ReadAllText(At(fs, root, "profile/Saves/sparse-update-user-save.bin")), Is.EqualTo("independent Windows update save sentinel"));
        string receipt = At(fs, evidence, "full-size-legacy-adoption.json");
        fs.File.WriteAllText(receipt, JsonSerializer.Serialize(new
        {
            ExactNativeCliAdoption = true, FixtureAuthority = AUTHORITY, ProductionAuthenticationProven = false,
            StockArtifactAvailable = false, TargetFiles = target.Files.Count, TargetBytes = target.TotalBytes,
            TargetDownloadRequests = fixture.Requests.Count, TargetStagedBytes = 0, StockArchiveSha256 = archiveSha,
            InstallationId = fixture.Entry.Id, InstallRoot = install, LauncherStorage = output, UserModAndSavePreserved = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(receipt, "Full-size native CLI adoption with a local fixture authority.");
    }

    /// <summary>
    /// Frozen native source bundle and WEM inputs pass fixture-authority transport, staging and full coordinator operations.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativePreparationProbe_ExactSourceBundleAndWemUpdateRepairUninstall()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_FULL_PLAYER_PROBE_BINDING");
        if (!string.IsNullOrWhiteSpace(binding))
        {
            if (!string.IsNullOrWhiteSpace(SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_PREPARATION_PROBE")))
                throw new InvalidDataException("Select component mode or an explicit full-player binding.");
            await RunBoundFullPlayerLifecycleAsync(binding);
            return;
        }
        string? frozen = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_PREPARATION_PROBE");
        if (string.IsNullOrWhiteSpace(frozen))
        {
            Assert.Ignore("Set KSP2REDUX_PREPARATION_PROBE to a retained frozen release-pack run for this opt-in integration.");
            return;
        }
        var fs = new RealFileSystem();
        string workspace = fs.Path.GetDirectoryName(typeof(FullSourcePreparationServiceTest).Assembly.Location)!;
        while (!fs.Directory.Exists(At(fs, workspace, "Documentation/Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("The evidence workspace was not found.");
        }
        string evidenceRoot = At(fs, workspace, "Library/Omnipatching");
        frozen = fs.Path.GetFullPath(frozen);
        if (!frozen.StartsWith(evidenceRoot + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The native probe input must be a frozen run below this checkout's Library/Omnipatching.");
        // The native transaction creates sibling candidates beside deeply nested audio files.
        string root = At(fs, evidenceRoot, "lp-" + Guid.NewGuid().ToString("N")[..12]);
        if (fs.Directory.Exists(root) || fs.File.Exists(root))
            throw new IOException("The native preparation fixture root already exists.");
        fs.Directory.CreateDirectory(root);
        using var prior = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, frozen, "result.json")));
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(prior.RootElement.GetProperty("Publication").GetRawText())!;
        StockBaselineManifest stock = StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(fs.File.ReadAllText(At(fs, frozen, "baseline.json"))));
        StockBaselineManifest target = StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(fs.File.ReadAllText(At(fs, frozen, "target.json"))));
        string artifact = At(fs, frozen, "artifact");
        var fixture = new Fixture(fs, root, At(fs, frozen, "frozen-baseline"), stock, target, artifact, publication);
        fs.File.WriteAllBytes(At(fs, root, "fixture-feed.json"), JsonSerializer.SerializeToUtf8Bytes(fixture.Feed()));
        fs.File.WriteAllBytes(At(fs, root, "inputs.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            FrozenInput = frozen, FixtureAuthority = AUTHORITY, ProductionAuthenticationProven = false,
            CompleteGameBaselineProven = false, UnityCompilationOrGameplayVerified = false,
            publication.ArtifactSha256, publication.TargetManifestSha256,
            StockManifestSha256 = Digest(stock.ToJson()), Installation = fixture.Install,
            stock.TotalBytes, TargetBytes = target.TotalBytes
        }));
        string inputs = At(fs, root, "inputs.json");
        TestContext.AddTestAttachment(inputs, "Explicit native fixture authority and frozen input pins.");
        var timer = Stopwatch.StartNew();
        long allocationStart = GC.GetTotalAllocatedBytes();
        try
        {
            using (var services = (ServiceProvider)Services(fixture))
            {
                var plan = new InstallPlan
                {
                    FullSourceVersion = VERSION, FullSourceChannel = CHANNEL, FullSourceReleaseId = RELEASE_ID
                };
                await services.GetRequiredService<IInstallPlanService>().ApplyToFolder(plan, fixture.Install,
                    _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
            }
            string stages = At(fs, fixture.Storage, "full-source-preparation-v1/stages");
            Assert.That(fs.Directory.EnumerateFileSystemEntries(stages), Is.Empty,
                "The ordinary native install should consume its private verified same-volume stage.");
            FullSourceInstallationService coordinator = fixture.Coordinator();
            AssertTreeStreaming(fs, fixture.Install, target);
            string archive = At(fs, fixture.Install, "uninstall.zip");
            string originalArchiveHash = HashFile(fs, archive);
            var next = fixture.Patch.FullSource! with { ReleaseId = "fixture-release-three", Version = "2.0.0.1.201" };
            fixture.Patch = fixture.Patch with { Version = next.Version, FullSource = next };
            FullSourcePreparedTarget update = await fixture.Preparation.FetchTargetForInstalledReleaseAsync(next.ReleaseId,
                CHANNEL, null, fixture.Entry.Id, coordinator.GetPublishedIdentity(fixture.Entry.Id)!, CancellationToken.None);
            Assert.That(update.TargetRetention!.Paths, Has.Count.EqualTo(target.Files.Count));
            Assert.That(fs.Directory.EnumerateFiles(update.SourceRoot, "*", SearchOption.AllDirectories), Is.Empty);
            await coordinator.ApplyAsync(fixture.Entry.Id, update, null, false, _ => { }, CancellationToken.None);
            Assert.That(coordinator.GetPublishedIdentity(fixture.Entry.Id)!.Version, Is.EqualTo(next.Version));
            var media = target.Files.First(file => file.Path.EndsWith(".wem", StringComparison.Ordinal));
            fs.File.WriteAllBytes(At(fs, fixture.Install, media.Path), [0, 1, 2]);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await coordinator.ApplyAsync(fixture.Entry.Id, update, null, false, _ => { }, CancellationToken.None));
            var repair = await fixture.Preparation.FetchRepairTargetAsync(next.ReleaseId, CHANNEL, null,
                fixture.Entry.Id, CancellationToken.None);
            Assert.That(fs.Directory.EnumerateFiles(repair.SourceRoot, "*", SearchOption.AllDirectories),
                Is.EqualTo(new[] { At(fs, repair.SourceRoot, media.Path) }));
            await coordinator.ApplyAsync(fixture.Entry.Id, repair, null, true, _ => { }, CancellationToken.None);
            AssertTreeStreaming(fs, fixture.Install, target);
            fixture.Offline = true;
            await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
            AssertTreeStreaming(fs, fixture.Install, stock);
            Assert.That(HashFile(fs, archive), Is.EqualTo(originalArchiveHash));
            Assert.That(await fixture.Coordinator().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
            AssertTreeStreaming(fs, At(fs, frozen, "frozen-baseline"), stock);
            AssertTreeStreaming(fs, At(fs, frozen, "frozen-target"), target);
            var artifactManifest = new ReleaseRecipePipeline(fs).Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
            for (int index = 0; index < artifactManifest.Packs.Count; index++)
            {
                Assert.That(HashFile(fs, At(fs, artifact, $"pack-{index:D6}.bin")), Is.EqualTo(artifactManifest.Packs[index].Sha256));
            }
            using var process = Process.GetCurrentProcess();
            string report = At(fs, root, "result.json");
            fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
            {
                FixtureAuthority = AUTHORITY, ProductionAuthenticationProven = false,
                CompleteGameBaselineProven = false, NativeWindowsFilesystemVerified = true,
                UnityCompilationOrGameplayVerified = false, FeedToPacksToStageVerified = true,
                CoordinatorInstallUpdateRepairUninstallVerified = true, OfflineStockRestoreVerified = true,
                SameVolumePreparedStageTransferred = true,
                ArchiveBackedStockRestorePayloadRetirementVerified = true,
                ExactTargetFiles = target.Files, ExactRestoredFiles = stock.Files,
                ImmutableStockArchiveSha256 = originalArchiveHash, TargetBytes = target.TotalBytes, StockBytes = stock.TotalBytes,
                publication.StoredPackBytes, fixture.Patch.Size,
                TransportRequests = fixture.Requests.Select(request => new { request.Url, request.Offset }).ToArray(),
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                RepairStagedBytes = media.Length, RepairRetainedFiles = repair.TargetRetention!.Paths.Count,
                UpdateRetainedFiles = update.TargetRetention.Paths.Count, UpdateStagedBytes = 0,
                CumulativeManagedAllocatedBytes = GC.GetTotalAllocatedBytes() - allocationStart,
                ProcessLifetimePeakWorkingSetBytes = process.PeakWorkingSet64,
                MeasurementIncludesStandaloneRunnerAndAssertions = true,
                FrozenBaselineAndTargetFilesUnchanged = true, InputPacksUnchanged = true,
                InputArtifactManifestUnchanged = HashFile(fs, At(fs, artifact, "release.json")) == publication.ArtifactSha256,
                PreparationStageDirectory = stages, UpdatedSourceRoot = update.SourceRoot
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(report, "Native preparation and exact coordinator outcomes, qualified as fixture evidence.");
            TestContext.Progress.WriteLine($"Native preparation evidence: {report}");
        }
        catch (Exception failure)
        {
            fs.File.WriteAllBytes(At(fs, root, "failure.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                FixtureAuthority = AUTHORITY, Type = failure.GetType().FullName, failure.Message,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
            }));
            throw;
        }
    }

    private static async Task RunBoundFullPlayerLifecycleAsync(string path)
    {
        var fs = new RealFileSystem();
        FullPlayerProbeBinding first = FullPlayerProbeBinding.Read(fs, path);
        FullPlayerProbeBinding next = first.ReadSuccessor();
        string workspace = fs.Path.GetDirectoryName(typeof(FullSourcePreparationServiceTest).Assembly.Location)!;
        while (!fs.Directory.Exists(At(fs, workspace, "Documentation/Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("The current checkout was not found.");
        }
        first.ValidatePairOutput(workspace, next);
        first.VerifyInputs();
        next.VerifyTree(next.TargetRoot, next.Target);
        ReleaseRecipePublication publication = first.ReadPublication();
        ReleaseRecipePublication successor = next.ReadPublication();
        string root = first.FixtureRoot;
        long initialBytes = checked(first.Stock.TotalBytes + fs.FileInfo.New(first.ArchivePath).Length + 64L * 1024 * 1024);
        long freeBytes = fs.DriveInfo.New(fs.Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (freeBytes < initialBytes)
            throw new IOException("The private stock installation and independent original ZIP do not fit. No fixture was created.");
        fs.Directory.CreateDirectory(root);
        var phases = new List<object>();
        Fixture? fixture = null;
        void Checkpoint(string phase, object evidence)
        {
            phases.Add(new { Phase = phase, Utc = DateTime.UtcNow, Evidence = evidence });
            fs.File.WriteAllBytes(At(fs, root, "checkpoint.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                DiagnosticOnly = true, ReleaseEligible = false, FinalBuildReady = false, FullGoalComplete = false,
                BindingSha256 = first.BindingSha256, SuccessorBindingSha256 = next.BindingSha256,
                FixtureAuthority = AUTHORITY, ProductionAuthenticationProven = false, Phases = phases
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Checkpoint("inputs-verified-before-copy", new { InitialFixtureBytes = initialBytes, ObservedFreeBytes = freeBytes,
            StockFiles = first.Stock.Files.Count, TargetFiles = first.Target.Files.Count, SuccessorFiles = next.Target.Files.Count,
            first.ArchiveSha256, first.ChangedPayloadPath });
        try
        {
            fixture = new Fixture(fs, root, first.StockRoot, first.Stock, first.Target,
                At(fs, first.PublicationRoot, "artifact"), publication, fullPlayer: first);
            string archive = At(fs, fixture.Install, "uninstall.zip");
            Assert.That(HashFile(fs, archive), Is.EqualTo(first.ArchiveSha256).IgnoreCase);
            async Task Command(string phase, FullPlayerProbeBinding selected, string operation)
            {
                using var output = new StringWriter();
                var context = new CliContext(Services(fixture), new CliOutput(output, isJson: true));
                int code = operation switch
                {
                    "install" => await new InstallCommand().RunWithContextAsync(context, new InstallSettings
                    {
                        Version = selected.Version, Channel = selected.Channel, Install = fixture.Entry.Id.ToString()
                    }),
                    "update" => await new UpdateCommand().RunWithContextAsync(context, new UpdateSettings
                    {
                        Channel = selected.Channel, Install = fixture.Entry.Id.ToString()
                    }),
                    _ => await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
                    {
                        Operation = operation, Release = selected.ReleaseId, Channel = selected.Channel,
                        Install = fixture.Entry.Id.ToString(), AssumeYes = operation == "uninstall"
                    })
                };
                fs.File.WriteAllText(At(fs, root, phase + ".cli.jsonl"), output.ToString());
                Checkpoint(phase, new { ExitCode = code, Operation = operation });
                Assert.That(code, Is.Zero, output.ToString());
            }
            void Select(FullPlayerProbeBinding selected, ReleaseRecipePublication published)
            {
                fixture.FullPlayer = selected;
                fixture.Artifact = At(fs, selected.PublicationRoot, "artifact");
                fixture.Patch = new ReleaseRecipePipeline(fs).CreateFeedEntry(fixture.Artifact, published,
                    selected.ReleaseId, selected.Channel, selected.Version,
                    "https://fixture.test/build/release.json", "https://fixture-backup.test/build/release.json",
                    new() { new FullSourceBaselineDefinition(first.Stock.ToJson(), new()) }, false, DateTime.UtcNow);
            }
            void VerifyInstalled(StockBaselineManifest expected)
            {
                AssertTreeStreaming(fs, fixture.Install, expected);
                Assert.That(fs.File.ReadAllText(At(fs, fixture.Install, "mods/user.txt")), Is.EqualTo("unknown user bytes retained"));
                Assert.That(HashFile(fs, archive), Is.EqualTo(first.ArchiveSha256).IgnoreCase);
                var allowed = expected.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                allowed.UnionWith(new[] { "mods/user.txt", "uninstall.zip", ".redux-transaction.lock" });
                foreach (string file in fs.Directory.EnumerateFiles(fixture.Install, "*", SearchOption.AllDirectories))
                {
                    Assert.That(allowed.Contains(fs.Path.GetRelativePath(fixture.Install, file).Replace('\\', '/')), Is.True,
                        "Unexpected retained or release-only file: " + file);
                }
                foreach (string directory in expected.Directories)
                {
                    Assert.That(fs.Directory.Exists(At(fs, fixture.Install, directory)), Is.True, directory);
                }
                var allowedDirectories = expected.Directories.Append("mods").ToHashSet(StringComparer.Ordinal);
                foreach (string directory in fs.Directory.EnumerateDirectories(fixture.Install, "*", SearchOption.AllDirectories))
                {
                    Assert.That(allowedDirectories.Contains(fs.Path.GetRelativePath(fixture.Install, directory).Replace('\\', '/')), Is.True,
                        "Unexpected retained or release-only directory: " + directory);
                }
            }
            await Command("install-a", first, "install");
            VerifyInstalled(first.Target);
            Select(next, successor);
            FullSourcePreparedTarget prepared = await fixture.Preparation.FetchTargetAsync(next.ReleaseId, next.Channel, CancellationToken.None);
            int index = next.Target.Files.Select((file, ordinal) => (file, ordinal))
                .Single(row => row.file.Path == first.ChangedPayloadPath).ordinal;
            bool interrupted = false;
            Checkpoint("before-changed-payload-interruption", new { prepared.SourceRoot, TargetIndex = index,
                publication.ArtifactSha256, SuccessorArtifactSha256 = successor.ArtifactSha256 });
            await Assert.ThrowsAsync<IOException>(async () => await fixture.Coordinator(engineFault: (name, after) =>
            {
                if (!interrupted && after && name == $"install-{index}")
                {
                    interrupted = true;
                    throw new IOException("Injected private full-player changed-payload publication interruption.");
                }
            }).ApplyAsync(fixture.Entry.Id, prepared, null, false, _ => { }, CancellationToken.None));
            Assert.That(interrupted, Is.True);
            var recovered = fixture.Coordinator().Reconcile(fixture.Entry.Id)!;
            var repeated = fixture.Coordinator().Reconcile(fixture.Entry.Id)!;
            Assert.That(recovered.Generation.ToJson(), Is.EqualTo(repeated.Generation.ToJson()));
            Assert.That(recovered.Generation.ToJson(), Is.EqualTo(first.Target.ToJson()));
            VerifyInstalled(first.Target);
            Checkpoint("recovery-repeated", new { Result = recovered.TransactionResult?.ToString(),
                recovered.Generation.BaselineIdentity, ArchiveFailure = recovered.ArchiveFailure?.Message });
            Assert.That(recovered.ArchiveFailure, Is.Null);
            await Command("changed-payload-update-b", next, next.Version == first.Version ? "apply" : "update");
            VerifyInstalled(next.Target);
            string damaged = At(fs, fixture.Install, first.ChangedPayloadPath);
            fs.File.WriteAllBytes(damaged, new byte[] { 0, 1, 2 });
            Checkpoint("damage-observed", new { Path = first.ChangedPayloadPath, Bytes = fs.FileInfo.New(damaged).Length,
                Sha256 = HashFile(fs, damaged) });
            await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Coordinator().ApplyAsync(
                fixture.Entry.Id, prepared, null, false, _ => { }, CancellationToken.None));
            await Command("explicit-repair-b", next, "repair");
            VerifyInstalled(next.Target);
            Select(first, publication);
            await Command("downgrade-a", first, "apply");
            VerifyInstalled(first.Target);
            fixture.Offline = true;
            await Command("offline-uninstall", first, "uninstall");
            VerifyInstalled(first.Stock);
            var terminal = fixture.Coordinator().Reconcile(fixture.Entry.Id)!;
            Assert.That(terminal.Generation.ToJson(), Is.EqualTo(first.Stock.ToJson()));
            Assert.That(terminal.ArchiveFailure, Is.Null);
            first.VerifyInputs();
            next.VerifyTree(next.TargetRoot, next.Target);
            Checkpoint("complete", new { InstallChangedUpdateRepairRollbackDowngradeUninstallVerified = true, StockFiles = first.Stock.Files.Count,
                StockDirectories = first.Stock.Directories.Count, first.ArchiveSha256,
                UnityCompilationOrGameplayVerified = false, ChannelSwitchVerified = false,
                UnknownSentinelPreserved = true, UnknownCollisionRefusalVerified = false,
                RepairRollbackToObservedDamageVerified = false, FullLifecycleAcceptance = false,
                DownloadInterruptionVerified = false, RealPowerLossVerified = false });
            fs.File.Copy(At(fs, root, "checkpoint.json"), At(fs, root, "result.json"), false);
            TestContext.AddTestAttachment(At(fs, root, "result.json"), "Diagnostic-only complete Windows player lifecycle.");
        }
        catch (Exception failure)
        {
            Checkpoint("failed-retained-for-reconciliation", new { Type = failure.GetType().FullName, failure.Message,
                Installation = fixture?.Install });
            throw;
        }
    }

    private static void AssertTreeStreaming(RealFileSystem fs, string root, StockBaselineManifest expected)
    {
        foreach (var file in expected.Files)
        {
            string path = At(fs, root, file.Path);
            Assert.That(fs.FileInfo.New(path).Length, Is.EqualTo(file.Length), file.Path);
            Assert.That(HashFile(fs, path), Is.EqualTo(file.Sha256), file.Path);
        }
    }

    private static string HashFile(RealFileSystem fs, string path)
    {
        using Stream stream = fs.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
