using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    /// <summary>
    /// Exercise real final player recipes through Windows preparation and coordinator recovery in one owned fixture.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeFinalPlayer_CompleteWindowsLifecycleAsync()
    {
        string? configPath = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_FINAL_PLAYER_PROBE");
        if (string.IsNullOrEmpty(configPath))
        {
            Assert.Ignore("Use launcher-final-windows-r1/release-identity-r1/continue.py with real players and their exact publisher feeds.");
            return;
        }
        var fs = new RealFileSystem();
        var paths = new InstallTransactionEngine(fs);
        paths.ValidateControlledPath(configPath);
        using var document = JsonDocument.Parse(fs.File.ReadAllBytes(configPath));
        JsonElement options = document.RootElement;
        Assert.That(options.GetProperty("Schema").GetString(), Is.EqualTo("omnipatching.final-windows-player-probe.v2"));
        string area = fs.Path.GetDirectoryName(configPath)!;
        string workspace = fs.Path.GetDirectoryName(typeof(FullSourcePreparationServiceTest).Assembly.Location)!;
        while (!fs.Directory.Exists(At(fs, workspace, "Documentation/Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("The launcher fixture checkout was not found.");
        }
        Assert.That(area, Is.EqualTo(At(fs, workspace, "Library/Omnipatching/launcher-final-windows-r1/release-identity-r1")));
        string output = options.GetProperty("OutputRoot").GetString()!;
        paths.ValidateControlledPath(output);
        Assert.That(output.StartsWith(fs.Path.Combine(area, "f") + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal), Is.True);
        Assert.That(fs.Directory.Exists(output) || fs.File.Exists(output), Is.False, "Never resume a retired or partially created fixture automatically.");
        CheckFinalFeedback(fs, options, workspace);
        string stockPath = CheckFinalPin(fs, paths, options.GetProperty("StockManifest"));
        string originalArchive = CheckFinalPin(fs, paths, options.GetProperty("StockArchive"));
        StockBaselineManifest stock = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(stockPath));
        string originalHash = HashFile(fs, originalArchive);
        var archiveService = new StockArchive(fs, new ZipFileService(fs));
        var pipeline = new ReleaseRecipePipeline(fs);
        JsonElement[] profiles = options.GetProperty("Profiles").EnumerateArray().ToArray();
        Assert.That(profiles, Has.Length.EqualTo(2));
        var targets = new List<StockBaselineManifest>();
        var publications = new List<ReleaseRecipePublication>();
        var feeds = new List<ReleaseManifest>();
        var patches = new List<ReleasePatch>();
        foreach (JsonElement profile in profiles)
        {
            string targetPath = CheckFinalPin(fs, paths, profile.GetProperty("TargetManifest"));
            string publicationPath = CheckFinalPin(fs, paths, profile.GetProperty("Publication"));
            string receiptPath = CheckFinalPin(fs, paths, profile.GetProperty("BuildReceipt"));
            using var receipt = JsonDocument.Parse(fs.File.ReadAllBytes(receiptPath));
            Assert.That(receipt.RootElement.GetProperty("schema").GetString(), Is.EqualTo("omnipatching.source-player-build.v1"));
            Assert.That(receipt.RootElement.GetProperty("outputOwnership").GetProperty("unityBuildSucceeded").GetBoolean(), Is.True);
            string player = profile.GetProperty("PlayerRoot").GetString()!;
            string artifact = profile.GetProperty("ArtifactRoot").GetString()!;
            foreach (string input in new[] { player, artifact, targetPath, publicationPath, originalArchive, stockPath })
            {
                paths.ValidateControlledPath(input);
                Assert.That(paths.PathsOverlap(output, input), Is.False);
            }
            StockBaselineManifest target = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(targetPath));
            AssertTreeStreaming(fs, player, target);
            foreach (string relative in new[] { "mods/user.txt", "saves/omnipatching-preserve.bin" })
            {
                Assert.That(stock.Files.Concat(target.Files).Any(file => file.Path == relative), Is.False);
                Assert.That(stock.Directories.Concat(target.Directories).Contains(relative), Is.False);
            }
            using var publicationDocument = JsonDocument.Parse(fs.File.ReadAllBytes(publicationPath));
            var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(publicationDocument.RootElement.GetProperty("Publication"))!;
            ReleaseRecipeManifest manifest = pipeline.Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
            (ReleaseManifest feed, ReleasePatch patch) = ReadFinalRelease(fs, paths, profile, stock);
            using (ModuleDefinition module = ModuleDefinition.ReadModule(At(fs, player, "KSP2_x64_Data/Managed/Ksp2Redux.dll")))
            {
                CheckFinalPlayerIdentity(module, patch);
            }
            Assert.That(manifest.TargetManifest, Is.EqualTo(target.ToJson()));
            Assert.That(manifest.Variants, Has.Count.EqualTo(1));
            Assert.That(manifest.Variants[0].SourceManifest, Is.EqualTo(stock.ToJson()));
            for (int index = 0; index < manifest.Packs.Count; index++)
            {
                string pack = At(fs, artifact, $"pack-{index:D6}.bin");
                paths.ValidateControlledPath(pack);
                Assert.That(fs.FileInfo.New(pack).Length, Is.EqualTo(manifest.Packs[index].Length));
                Assert.That(HashFile(fs, pack), Is.EqualTo(manifest.Packs[index].Sha256));
            }
            targets.Add(target);
            publications.Add(publication);
            feeds.Add(feed);
            patches.Add(patch);
        }
        Assert.That(targets[1].Files.Any(file => !targets[0].Files.Contains(file)) ||
            targets[0].Files.Any(file => !targets[1].Files.Contains(file)), Is.True, "Updates require an actual file-content change.");
        Assert.That(CompareFinalVersions(patches[1].Version, patches[0].Version), Is.GreaterThan(0),
            "Missing update/downgrade scenario: the second intended release must be newer.");
        Assert.That(feeds[1].Channel, Is.Not.EqualTo(feeds[0].Channel), "Missing real channel-switch scenario.");
        await archiveService.VerifyAsync(originalArchive, stock);
        string validationPath = options.GetProperty("ValidationResult").GetString()!;
        paths.ValidateControlledPath(validationPath);
        Assert.That(validationPath, Is.EqualTo(fs.Path.ChangeExtension(configPath, ".validation.json")));
        Assert.That(fs.File.Exists(validationPath), Is.False);
        fs.File.WriteAllBytes(validationPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            InputConfigSha256 = HashFile(fs, configPath),
            ReleaseIdentities = patches.Select(patch => new { patch.FullSource!.ReleaseId, patch.FullSource.Channel, patch.Version }),
            FixtureAuthorityOnly = true, ProductionAuthenticationProven = false
        }));
        if (options.GetProperty("ValidateOnly").GetBoolean())
        {
            TestContext.Progress.WriteLine("Final player inputs verified. No fixture created and no transaction dispatched.");
            return;
        }
        CheckFinalFeedback(fs, options, workspace);
        long free = fs.DriveInfo.New(fs.Path.GetPathRoot(output)!).AvailableFreeSpace;
        Assert.That(free, Is.GreaterThanOrEqualTo(options.GetProperty("PlannedAdditionalBytes").GetInt64()));
        fs.Directory.CreateDirectory(output);
        string install = At(fs, output, "game");
        await archiveService.RestoreToNewDirectoryAsync(originalArchive, stock, install);
        var fixture = new Fixture(fs, output, install, stock, targets[0], profiles[0].GetProperty("ArtifactRoot").GetString()!,
            publications[0], initializeStock: false, suppliedFeed: feeds[0], suppliedPatch: patches[0]);
        string archive = At(fs, install, "uninstall.zip");
        string archiveHash = "";
        string save = At(fs, install, "saves/omnipatching-preserve.bin");
        Put(fs, install, "saves/omnipatching-preserve.bin", Encoding.UTF8.GetBytes("isolated preservation sentinel"));
        string saveHash = HashFile(fs, save);
        string modHash = HashFile(fs, At(fs, install, "mods/user.txt"));
        var phases = new List<string>();
        try
        {
            Select(0);
            await ApplyPlanAsync();
            archiveHash = HashFile(fs, archive);
            await archiveService.VerifyAsync(archive, stock);
            CheckGeneration(0, stock);
            phases.Add("fresh-in-place-install-and-complete-stock-ZIP-creation");
            StockBaselineFile damaged = targets[0].Files.First(file => file.Path != "source-player-build.json" && file.Length > 0);
            Put(fs, install, damaged.Path, [17, 29, 41]);
            FullSourcePreparedTarget target = await fixture.Preparation.FetchTargetAsync(patches[0].FullSource!.ReleaseId, feeds[0].Channel, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, null, false, _ => { }, CancellationToken.None));
            await fixture.Coordinator().ApplyAsync(fixture.Entry.Id, target, null, true, _ => { }, CancellationToken.None);
            CheckGeneration(0, stock);
            phases.Add("reject-drift-and-explicit-repair");
            Select(1);
            FullSourcePreparedTarget next = await fixture.Preparation.FetchTargetAsync(patches[1].FullSource!.ReleaseId, feeds[1].Channel, CancellationToken.None);
            bool interrupted = false;
            var faultEngine = new InstallTransactionEngine(fs, fixture.Space.Object.GetAvailableFreeSpace, (name, after) =>
            {
                if (!interrupted && after && (name.StartsWith("install-", StringComparison.Ordinal) || name.StartsWith("remove-", StringComparison.Ordinal)))
                {
                    interrupted = true;
                    throw new IOException("Isolated final-player interruption after an actual owned-file mutation.");
                }
            });
            var faultCoordinator = new FullSourceInstallationService(fs, fixture.Configuration.Object, new ZipFileService(fs),
                fixture.Space.Object, fixture.Preparation, fixture.Os.Object, Mock.Of<ILogService>(), Mock.Of<IGameProcessService>(),
                new InstallFileLockService(fs, fixture.Os.Object), engine: faultEngine);
            await Assert.ThrowsAsync<IOException>(async () =>
                await faultCoordinator.ApplyAsync(fixture.Entry.Id, next, null, false, _ => { }, CancellationToken.None));
            Assert.That(interrupted, Is.True);
            Assert.That(fixture.Coordinator().Reconcile(fixture.Entry.Id)!.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
            CheckGeneration(0, targets[1]);
            fixture.Coordinator().Reconcile(fixture.Entry.Id);
            phases.Add("partial-publication-interruption-fresh-recovery-rollback-and-repeat");
            bool committedLoss = false;
            await Assert.ThrowsAsync<IOException>(async () =>
                await fixture.Coordinator((name, after) =>
                {
                    if (name == "engine-apply" && after)
                    {
                        committedLoss = true;
                        throw new IOException("Isolated final-player loss after engine commit.");
                    }
                }).ApplyAsync(fixture.Entry.Id, next, null, false, _ => { }, CancellationToken.None));
            Assert.That(committedLoss, Is.True);
            fixture.Offline = true;
            Assert.That(fixture.Coordinator().Reconcile(fixture.Entry.Id)!.Release, Is.EqualTo(next.Identity));
            fixture.Offline = false;
            CheckGeneration(1, targets[0]);
            phases.Add("committed-publication-offline-recovery");
            Select(0);
            await ApplyPlanAsync();
            CheckGeneration(0, targets[1]);
            phases.Add("actual-byte-downgrade-and-channel-switch");
            Select(1);
            await ApplyPlanAsync();
            CheckGeneration(1, targets[0]);
            phases.Add("actual-byte-update-and-channel-switch");
            fixture.Offline = true;
            await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
            AssertTreeStreaming(fs, install, stock);
            foreach (StockBaselineFile added in targets.SelectMany(value => value.Files).Where(file => !stock.Files.Any(original => original.Path == file.Path)))
            {
                Assert.That(fs.File.Exists(At(fs, install, added.Path)), Is.False, added.Path);
            }
            Preserve();
            phases.Add("offline-full-stock-uninstall-and-payload-retirement");
            // Reuse the restored fixture with a new configured identity. Preserve the old catalog and archive.
            fixture = new Fixture(fs, output, install, stock, targets[0], profiles[0].GetProperty("ArtifactRoot").GetString()!,
                publications[0], initializeStock: false, suppliedFeed: feeds[0], suppliedPatch: patches[0]);
            Select(0);
            FullSourceStockPreparation adopted = await fixture.Preparation.FetchStockAsync(fixture.Entry.Id, CancellationToken.None);
            Assert.That(adopted.SourceRoot, Is.Empty, "Canonical ZIP adoption must not restore another stock tree.");
            Assert.That(adopted.ExistingArchive, Is.Not.Null);
            await ApplyPlanAsync();
            CheckGeneration(0, stock);
            fixture.Offline = true;
            await fixture.Coordinator().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);
            AssertTreeStreaming(fs, install, stock);
            Preserve();
            phases.Add("existing-complete-ZIP-adoption-and-second-offline-uninstall-without-stock-recopy");
            fs.File.WriteAllBytes(At(fs, output, "result.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                Success = true, Phases = phases, StockBytes = stock.TotalBytes, TargetBytes = targets.Select(value => value.TotalBytes),
                ImmutableStockArchiveSha256 = archiveHash, OriginalWitnessSha256 = originalHash, InputConfigSha256 = HashFile(fs, configPath),
                FixtureAuthorityOnly = true, ProductionAuthenticationProven = false, UnityOrGameplayVerified = false,
                ProcessLossSimulationOnly = true, UserSaveAndModSentinelsPreserved = true
            }));
        }
        catch (Exception failure)
        {
            fs.File.WriteAllBytes(At(fs, output, "failure.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                Type = failure.GetType().FullName, failure.Message, CompletedPhases = phases
            }));
            throw;
        }

        void Select(int index)
        {
            CheckFinalFeedback(fs, options, workspace);
            string artifact = profiles[index].GetProperty("ArtifactRoot").GetString()!;
            fixture.SuppliedFeed = feeds[index];
            fixture.Patch = patches[index];
            fixture.ResponseOverride = (url, offset, _) =>
            {
                Stream stream = fs.File.OpenRead(At(fs, artifact, new Uri(url).Segments.Last()));
                long length = stream.Length;
                stream.Position = offset;
                var response = new HttpResponseMessage(offset == 0 ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(stream)
                };
                response.Content.Headers.ContentLength = length - offset;
                if (offset > 0)
                    response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, length - 1, length);
                return response;
            };
        }

        async Task ApplyPlanAsync()
        {
            CheckFinalFeedback(fs, options, workspace);
            var feed = new ManifestReleasesFeed(fixture.Transport.Object, Mock.Of<ILogService>(), fixture.Storage, fixture.Config.Feeds.Single());
            Assert.That(await feed.UpdateManifest(), Is.True);
            GameVersion selected = feed.GetAllVersions().Single(version => $"{version.VersionNumber}.{version.BuildNumber}" == fixture.Patch.Version);
            InstallPlan plan = feed.GetPatchListToVersion(selected, selected);
            Assert.That(plan.FullSourceReleaseId, Is.EqualTo(fixture.Patch.FullSource!.ReleaseId));
            await Services(fixture).GetRequiredService<IInstallPlanService>().ApplyToFolder(plan, install,
                _ => { }, (_, _) => { }, (_, _) => { }, CancellationToken.None);
        }

        void CheckGeneration(int index, StockBaselineManifest previous)
        {
            AssertTreeStreaming(fs, install, targets[index]);
            foreach (StockBaselineFile removed in previous.Files.Where(file => !targets[index].Files.Any(targetFile => targetFile.Path == file.Path)))
            {
                Assert.That(fs.File.Exists(At(fs, install, removed.Path)), Is.False, removed.Path);
            }
            Preserve();
        }

        void Preserve()
        {
            Assert.That(HashFile(fs, save), Is.EqualTo(saveHash));
            Assert.That(HashFile(fs, At(fs, install, "mods/user.txt")), Is.EqualTo(modHash));
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveHash));
            Assert.That(HashFile(fs, originalArchive), Is.EqualTo(originalHash));
        }
    }

    private static (ReleaseManifest Feed, ReleasePatch Patch) ReadFinalRelease(IFileSystem fs,
        InstallTransactionEngine paths, JsonElement profile, StockBaselineManifest stock)
    {
        string feedPath = CheckFinalPin(fs, paths, profile.GetProperty("FeedManifest"));
        var feed = JsonSerializer.Deserialize<ReleaseManifest>(fs.File.ReadAllBytes(feedPath)) ??
            throw new InvalidDataException("Missing publisher feed.");
        ReleaseManifestValidator.Validate(feed);
        string releaseId = profile.GetProperty("ReleaseId").GetString()!;
        string version = profile.GetProperty("Version").GetString()!;
        string channel = profile.GetProperty("Channel").GetString()!;
        ReleasePatch[] matches = feed.Patches.Where(patch => patch.FullSource?.ReleaseId == releaseId).ToArray();
        if (matches.Length != 1 || feed.Patches.Count(patch => patch.Version == version) != 1)
            throw new InvalidDataException("Intended release must identify exactly one publisher entry.");
        ReleasePatch patch = matches[0];
        FullSourceReleaseDescriptor descriptor = patch.FullSource!;
        if (feed.SchemaVersion != 3 || feed.Channel != channel || descriptor.Channel != channel || patch.Version != version)
            throw new InvalidDataException("Intended release version/channel differs from the publisher feed.");
        string targetPath = CheckFinalPin(fs, paths, profile.GetProperty("TargetManifest"));
        string publicationPath = CheckFinalPin(fs, paths, profile.GetProperty("Publication"));
        string artifact = profile.GetProperty("ArtifactRoot").GetString()!;
        paths.ValidateControlledPath(artifact);
        using var publicationDocument = JsonDocument.Parse(fs.File.ReadAllBytes(publicationPath));
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(publicationDocument.RootElement.GetProperty("Publication"))!;
        var pipeline = new ReleaseRecipePipeline(fs, fs is MockFileSystem ? _ => { } : null);
        ReleaseRecipeManifest recipe = pipeline.Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
        if (!string.Equals(patch.ChecksumSha256, publication.ArtifactSha256, StringComparison.OrdinalIgnoreCase) ||
            patch.Size != fs.FileInfo.New(At(fs, artifact, "release.json")).Length ||
            !string.Equals(descriptor.TargetManifestSha256, publication.TargetManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !descriptor.TargetManifest.AsSpan().SequenceEqual(fs.File.ReadAllBytes(targetPath)) ||
            !descriptor.TargetManifest.AsSpan().SequenceEqual(recipe.TargetManifest) ||
            descriptor.Baselines.Count != 1 || recipe.Variants.Count != 1 ||
            !descriptor.Baselines[0].Manifest.AsSpan().SequenceEqual(stock.ToJson()) ||
            !descriptor.Baselines[0].Manifest.AsSpan().SequenceEqual(recipe.Variants[0].SourceManifest) ||
            descriptor.DownloadBytes != checked(patch.Size + recipe.Packs.Sum(pack => (long)pack.Length)))
            throw new InvalidDataException("Publisher target, recipe, packs or baseline differ from the supplied publication.");
        return (feed, patch);
    }

    private static int CompareFinalVersions(string left, string right)
    {
        uint[] leftParts = left.Split('.').Select(uint.Parse).ToArray();
        uint[] rightParts = right.Split('.').Select(uint.Parse).ToArray();
        for (int index = 0; index < 5; index++)
        {
            int result = leftParts[index].CompareTo(rightParts[index]);
            if (result != 0)
                return result;
        }
        return 0;
    }

    private static void CheckFinalPlayerIdentity(ModuleDefinition module, ReleasePatch patch)
    {
        GameVersion actual = GameVersion.FromVersionIDType(module.GetType("VersionID") ??
            throw new InvalidDataException("The production game assembly has no VersionID."), true);
        if ($"{actual.VersionNumber}.{actual.BuildNumber}" != patch.Version || actual.Channel != patch.FullSource!.Channel)
            throw new InvalidDataException("Publisher version/channel differs from the actual player VersionID.");
    }

    /// <summary>
    /// Reject publisher relabeling even when a target contract hashes the same game assembly.
    /// </summary>
    /// <param name="change">The publisher selector that differs from the compiled identity, or matching.</param>
    [TestCase("version"), TestCase("channel"), TestCase("matching")]
    public void FinalReleaseIdentity_CompiledPlayerSelectorsMustMatch(string change)
    {
        Fixture fixture = Create();
        using ModuleDefinition module = TestHelpers.GenerateMockVersionID(("VERSION_TEXT", VERSION),
            ("CHANNEL_NAME", CHANNEL), ("DEBUG_INFO", "fixture-build")).module;
        ReleasePatch patch = fixture.Patch;
        if (change == "version")
            patch = patch with { Version = "7.0.0.0.1", FullSource = patch.FullSource! with { Version = "7.0.0.0.1" } };
        if (change == "channel")
            patch = patch with { FullSource = patch.FullSource! with { Channel = "stable" } };
        if (change == "matching")
        {
            Assert.DoesNotThrow(() => CheckFinalPlayerIdentity(module, patch));
            return;
        }
        Assert.Throws<InvalidDataException>(() => CheckFinalPlayerIdentity(module, patch));
    }

    /// <summary>
    /// Refuse contradictory release intent or publisher bindings before invoking preparation.
    /// </summary>
    /// <param name="change">The conflicting identity or publication field.</param>
    [TestCase("intended-version"), TestCase("intended-channel"), TestCase("intended-release")]
    [TestCase("publisher-version"), TestCase("publisher-channel"), TestCase("target"), TestCase("recipe"), TestCase("packs")]
    public void FinalReleaseIdentity_ContradictionsRefuseBeforePreparation(string change)
    {
        Fixture fixture = Create();
        ReleasePatch patch = fixture.Patch;
        FullSourceReleaseDescriptor descriptor = patch.FullSource!;
        patch = change switch
        {
            "publisher-version" => patch with { Version = "7.0.0.0.1" },
            "publisher-channel" => patch with { FullSource = descriptor with { Channel = "stable" } },
            "target" => patch with { FullSource = descriptor with { TargetManifestSha256 = new string('0', 64) } },
            "recipe" => patch with { ChecksumSha256 = new string('0', 64) },
            "packs" => patch with { FullSource = descriptor with { DownloadBytes = descriptor.DownloadBytes + 1 } },
            _ => patch
        };
        fixture.Patch = patch;
        using JsonDocument profile = MockFinalProfile(fixture, change == "intended-release" ? "other-release" : RELEASE_ID,
            change == "intended-channel" ? "stable" : CHANNEL, change == "intended-version" ? "7.0.0.0.1" : VERSION);
        Assert.Throws<InvalidDataException>(() => ReadFinalRelease(fixture.Fs,
            new InstallTransactionEngine(fixture.Fs, validateFileStreams: _ => { }), profile.RootElement, fixture.Stock));
        Assert.That(fixture.Requests, Is.Empty, "Identity validation must precede artifact transport or preparation.");
    }

    /// <summary>
    /// Preserve a supplied mock publisher entry through the actual maintained feed and preparation consumers.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task FinalReleaseIdentity_SuppliedMockMetadataReachesMaintainedConsumersAsync()
    {
        Fixture fixture = Create();
        fixture.Patch = fixture.Patch with { Version = "3.1.4.1.25", FullSource = fixture.Patch.FullSource! with
            { ReleaseId = "publisher-declared-release", Channel = "stable", Version = "3.1.4.1.25" } };
        fixture.SuppliedFeed = new ReleaseManifest { SchemaVersion = 3, Channel = "stable", GeneratedAt = DateTime.UtcNow,
            Patches = [fixture.Patch] };
        using JsonDocument profile = MockFinalProfile(fixture, "publisher-declared-release", "stable", "3.1.4.1.25");
        (ReleaseManifest feed, ReleasePatch patch) = ReadFinalRelease(fixture.Fs,
            new InstallTransactionEngine(fixture.Fs, validateFileStreams: _ => { }), profile.RootElement, fixture.Stock);
        fixture.SuppliedFeed = feed;
        fixture.Patch = patch;
        var consumer = new ManifestReleasesFeed(fixture.Transport.Object, Mock.Of<ILogService>(), fixture.Storage, fixture.Config.Feeds.Single());
        Assert.That(await consumer.UpdateManifest(), Is.True);
        GameVersion selected = consumer.GetAllVersions().Single();
        InstallPlan plan = consumer.GetPatchListToVersion(selected, selected);
        Assert.That(plan.FullSourceReleaseId, Is.EqualTo("publisher-declared-release"));
        Assert.That(plan.FullSourceVersion, Is.EqualTo("3.1.4.1.25"));
        Assert.That(plan.FullSourceChannel, Is.EqualTo("stable"));
        FullSourcePreparedTarget prepared = await fixture.Preparation.FetchTargetAsync(plan.FullSourceReleaseId!, plan.FullSourceChannel!, CancellationToken.None);
        Assert.That(prepared.Identity.ReleaseId, Is.EqualTo(plan.FullSourceReleaseId));
        Assert.That(prepared.Identity.Version, Is.EqualTo(plan.FullSourceVersion));
        Assert.That(prepared.Identity.Channel, Is.EqualTo(plan.FullSourceChannel));
        AssertTree(fixture.Fs, prepared.SourceRoot, fixture.Target);
        string? export = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_RELEASE_IDENTITY_FIXTURE");
        if (!string.IsNullOrEmpty(export))
        {
            var real = new RealFileSystem();
            string workspace = real.Path.GetDirectoryName(typeof(FullSourcePreparationServiceTest).Assembly.Location)!;
            while (!real.Directory.Exists(At(real, workspace, "Documentation/Omnipatching")))
            {
                workspace = real.Path.GetDirectoryName(workspace)!;
            }
            Assert.That(real.Path.GetFullPath(export), Is.EqualTo(At(real, workspace,
                "Library/Omnipatching/launcher-final-windows-r1/release-identity-r1/mock-publisher-inputs")));
            Assert.That(real.Directory.Exists(export), Is.False, "Preserve previous generated fixture evidence.");
            real.Directory.CreateDirectory(export);
            foreach (string path in fixture.Fs.Directory.EnumerateFiles(fixture.Artifact, "*", SearchOption.AllDirectories))
            {
                string destination = At(real, export, "artifact/" + fixture.Fs.Path.GetFileName(path));
                real.Directory.CreateDirectory(real.Path.GetDirectoryName(destination)!);
                real.File.WriteAllBytes(destination, fixture.Fs.File.ReadAllBytes(path));
            }
            foreach ((string input, string output) in new[] { ("supplied-feed.json", "feed.json"), ("supplied-target.json", "target.json"),
                ("supplied-publication.json", "publication.json") })
            {
                real.File.WriteAllBytes(At(real, export, output), fixture.Fs.File.ReadAllBytes(At(fixture.Fs, fixture.Root, input)));
            }
            real.File.WriteAllBytes(At(real, export, "stock.json"), fixture.Stock.ToJson());
        }
    }

    private static JsonDocument MockFinalProfile(Fixture fixture, string releaseId, string channel, string version)
    {
        IFileSystem fs = fixture.Fs;
        string feed = At(fs, fixture.Root, "supplied-feed.json");
        string target = At(fs, fixture.Root, "supplied-target.json");
        string publication = At(fs, fixture.Root, "supplied-publication.json");
        fs.File.WriteAllBytes(feed, JsonSerializer.SerializeToUtf8Bytes(fixture.Feed()));
        fs.File.WriteAllBytes(target, fixture.Target.ToJson());
        string artifact = At(fs, fixture.Artifact, "release.json");
        fs.File.WriteAllBytes(publication, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Publication = new { ArtifactSha256 = FinalHash(fs, artifact), TargetManifestSha256 = FinalHash(fs, target) }
        }));
        object Pin(string path) => new { Path = path, Length = fs.FileInfo.New(path).Length, Sha256 = FinalHash(fs, path) };
        return JsonSerializer.SerializeToDocument(new
        {
            FeedManifest = Pin(feed), TargetManifest = Pin(target), Publication = Pin(publication), ArtifactRoot = fixture.Artifact,
            ReleaseId = releaseId, Channel = channel, Version = version
        });
    }

    private static string FinalHash(IFileSystem fs, string path)
    {
        using Stream stream = fs.File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string CheckFinalPin(IFileSystem fs, InstallTransactionEngine paths, JsonElement pin)
    {
        string path = pin.GetProperty("Path").GetString()!;
        paths.ValidateControlledPath(path);
        Assert.That(fs.FileInfo.New(path).Length, Is.EqualTo(pin.GetProperty("Length").GetInt64()));
        Assert.That(FinalHash(fs, path), Is.EqualTo(pin.GetProperty("Sha256").GetString()));
        return path;
    }

    private static void CheckFinalFeedback(RealFileSystem fs, JsonElement options, string workspace)
    {
        JsonElement feedback = options.GetProperty("Feedback");
        Assert.That(feedback.ValueKind, Is.EqualTo(JsonValueKind.Object), "Current parent feedback is required.");
        var paths = new InstallTransactionEngine(fs);
        string path = CheckFinalPin(fs, paths, feedback);
        string allowed = fs.Path.GetFullPath(fs.Path.Combine(workspace, "../.agents/omnipatching-work"));
        Assert.That(path.StartsWith(allowed + fs.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
    }
}
