using System.Diagnostics;
using System.Text.Json;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    /// <summary>
    /// Repair one damaged, privately owned file in the full r9 installation using the stock-backed release recipe.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeRetainedPlayer_RepairsR9FromStockDeliveryAsync()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R9_REPAIR");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the exact stock delivery and the verified isolated R79 r9 installation.");
            return;
        }
        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, "R79");
        string install = At(fs, root, "game");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, "stock-r9-repair-delivery-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        foreach (string path in new[] { root, install, output })
            paths.ValidateControlledPath(path);
        Assert.That(fs.File.Exists(At(fs, output, "repair-start.json")), Is.False, "Preserve the previous repair evidence.");
        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(install)!);
        Assert.That(drive.AvailableFreeSpace, Is.GreaterThanOrEqualTo(8L * 1024 * 1024 * 1024));
        string skipDirectory = At(fs, batch, "skipped-r7-r9-cli-r1");
        using var skip = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, skipDirectory, "skip-result.json")));
        Assert.That(skip.RootElement.GetProperty("ExactSkippedReleaseUpdate").GetBoolean(), Is.True);
        string release = skip.RootElement.GetProperty("FinalRelease").GetString()!;
        string archiveSha = skip.RootElement.GetProperty("StockArchiveSha256").GetString()!;
        var stock = ReadManifest(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json"));
        var r7 = ReadManifest(At(fs, skipDirectory, "r7-source-input.json"));
        var r9 = ReadManifest(At(fs, batch, "r9-integrated-runtime-r1/delta/r9-target-input.json"));
        using var published = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, output, "stock-publication.json")));
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(published.RootElement.GetProperty("publication"))!;
        string artifact = At(fs, output, "stock-artifact");
        const string VERSION_R9 = "2.0.0.1.202";
        var patch = new ReleaseRecipePipeline(fs).CreateFeedEntry(artifact, publication, release, CHANNEL, VERSION_R9,
            "https://fixture.test/repair/stock/release.json", "https://fixture.test/repair/stock/release.json",
            [new FullSourceBaselineDefinition(stock.ToJson(), [])], false, DateTime.UtcNow);
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = CHANNEL, GeneratedAt = DateTime.UtcNow, Patches = [patch] };
        var fixture = new Fixture(fs, root, At(fs, workspace, "RTX1/game"), stock, r9, artifact, publication,
            initializeStock: false, suppliedFeed: feed, suppliedPatch: patch);
        string catalog = fs.Directory.EnumerateDirectories(At(fs, fixture.Storage, "full-source-v1")).Single();
        fixture.Entry.Id = Guid.ParseExact(fs.Path.GetFileName(catalog)!, "N");
        fixture.Config.ActiveKsp2InstallId = fixture.Entry.Id;
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string _) => drive.AvailableFreeSpace);
        var current = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id);
        Assert.That(current?.ReleaseId, Is.EqualTo(release));
        Assert.That(current?.ManifestSha256, Is.EqualTo(Digest(r9.ToJson())));
        const string LEAF = "Redux/Addressables/StandaloneWindows64/ad9f80c9eaeafff7d4b127b97b29de3c.bundle";
        Assert.That(r7.Files.Any(file => file.Path == LEAF), Is.False, "The test must damage a private file created by the update.");
        StockBaselineFile expected = r9.Files.Single(file => file.Path == LEAF);
        string damaged = At(fs, install, LEAF);
        Assert.That(HashFile(fs, damaged), Is.EqualTo(expected.Sha256));
        string save = At(fs, root, "profile/save.bin");
        string mod = At(fs, install, "mods/skip-user.bin");
        string saveSha = HashFile(fs, save);
        string modSha = HashFile(fs, mod);
        Write("repair-start.json", new
        {
            Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId, Release = release,
            Path = LEAF, OriginalSha256 = expected.Sha256, InitialFree = drive.AvailableFreeSpace
        });
        fs.File.Move(damaged, At(fs, output, "pre-repair-original.bundle"));
        fs.File.WriteAllBytes(damaged, [0, 1, 2]);
        long downloaded = 0;
        fixture.ResponseOverride = (url, offset, _) =>
        {
            if (!new Uri(url).AbsolutePath.StartsWith("/repair/stock/", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected non-stock repair request: " + url);
            var response = fixture.Response(url, offset, artifact);
            Interlocked.Add(ref downloaded, response.Content.Headers.ContentLength ?? 0);
            return response;
        };
        var timer = Stopwatch.StartNew();
        using var log = new StringWriter();
        using var services = (ServiceProvider)Services(fixture);
        var context = new CliContext(services, new CliOutput(log, isJson: true));
        int code;
        try
        {
            code = await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
                { Operation = "repair", Channel = CHANNEL, Release = release, Install = fixture.Entry.Id.ToString() });
        }
        finally
        {
            fs.File.WriteAllText(At(fs, output, "repair.cli.jsonl"), log.ToString());
        }
        Assert.That(code, Is.Zero, log.ToString());
        AssertTreeStreaming(fs, install, r9);
        Assert.That(HashFile(fs, At(fs, install, "uninstall.zip")), Is.EqualTo(archiveSha));
        Assert.That(HashFile(fs, save), Is.EqualTo(saveSha));
        Assert.That(HashFile(fs, mod), Is.EqualTo(modSha));
        Assert.That(downloaded, Is.GreaterThan(0).And.LessThan(patch.FullSource!.DownloadBytes));
        Write("repair-result.json", new
        {
            ExactFullSizeRepair = true, Release = release, RepairedFile = expected, TargetFiles = r9.Files.Count,
            DownloadBytes = downloaded, DownloadRequests = fixture.Requests.Count, StockArchiveSha256 = archiveSha,
            UserSaveAndModPreserved = true, Seconds = timer.Elapsed.TotalSeconds, FinalFree = drive.AvailableFreeSpace
        });

        StockBaselineManifest ReadManifest(string path) => StockBaselineManifest.FromJson(fs.File.ReadAllBytes(path));

        void Write(string name, object value)
        {
            using Stream file = fs.FileStream.New(At(fs, output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Exercise an ordinary Windows update directly from r7 to r9 with the real r8 recipe still offered in the feed.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeRetainedPlayer_SkipsPublishedIntermediateReleaseAsync()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R7_R9_SKIP");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the prepared r7 fixture and the two real update recipes.");
            return;
        }
        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, "R79");
        string install = At(fs, root, "game");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, "skipped-r7-r9-cli-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        foreach (string path in new[] { root, install, output })
            paths.ValidateControlledPath(path);
        Assert.That(fs.File.Exists(At(fs, output, "fixture-ready.json")), Is.True);
        Assert.That(fs.File.Exists(At(fs, output, "skip-start.json")), Is.False, "Preserve the previous full test evidence.");
        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(install)!);
        long initialFree = drive.AvailableFreeSpace;
        Assert.That(initialFree, Is.GreaterThanOrEqualTo(17L * 1024 * 1024 * 1024));
        string middle = At(fs, batch, "r7-to-r8-exact-copy-recipe-r1");
        var stock = ReadManifest(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json"));
        var r7 = ReadManifest(At(fs, output, "r7-source-input.json"));
        var r8 = ReadManifest(At(fs, middle, "r8-target-input.json"));
        var r9 = ReadManifest(At(fs, batch, "r9-integrated-runtime-r1/delta/r9-target-input.json"));
        using var restore = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, batch, "full-r9-offline-stock-restore-r1/restore-result.json")));
        Assert.That(restore.RootElement.GetProperty("ExactOfflineRestore").GetBoolean(), Is.True);
        string archiveSha = restore.RootElement.GetProperty("StockArchiveSha256").GetString()!;
        string archive = At(fs, install, "uninstall.zip");
        string save = At(fs, root, "profile/save.bin");
        string mod = At(fs, install, "mods/skip-user.bin");
        string saveSha = HashFile(fs, save);
        string modSha = HashFile(fs, mod);
        var releaseTime = DateTime.UtcNow;
        var intermediate = ReadRetainedPlayerRoute(fs, At(fs, middle, "publication-result.json"), At(fs, middle, "artifact"),
            stock, r7, r8, "private-skip-r8", CHANNEL, "2.0.0.1.200", releaseTime.AddMinutes(-1));
        var latest = ReadRetainedPlayerRoute(fs, At(fs, output, "skipped-publication.json"), At(fs, output, "skipped-artifact"),
            stock, r7, r9, "private-skip-r9", CHANNEL, "2.0.0.1.202", releaseTime);
        var baseDescriptor = latest.Patch.FullSource! with
        {
            ReleaseId = "private-skip-r7", Version = "2.0.0.1.199", TargetManifest = r7.ToJson(), TargetManifestSha256 = Digest(r7.ToJson()),
            Baselines = [new FullSourceBaselineDefinition(stock.ToJson(), [], [r7.ToJson()])], UpdateArtifacts = null
        };
        var installedPatch = latest.Patch with
            { Version = baseDescriptor.Version, ReleasedAt = releaseTime.AddMinutes(-2), FullSource = baseDescriptor };
        var feed = latest.Feed with { Patches = [installedPatch, intermediate.Patch, latest.Patch] };
        ReleaseManifestValidator.Validate(feed);
        var fixture = new Fixture(fs, root, At(fs, workspace, "RTX1/game"), stock, r9, latest.Artifact, null!,
            initializeStock: false, suppliedFeed: feed, suppliedPatch: latest.Patch);
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string _) => drive.AvailableFreeSpace);
        Write("skip-start.json", new { Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId, initialFree });
        long minimumFree = initialFree;
        using var cancel = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                minimumFree = Math.Min(minimumFree, drive.AvailableFreeSpace);
                if (minimumFree < 5L * 1024 * 1024 * 1024)
                    cancel.Cancel();
                await Task.Delay(1000);
            }
        });
        try
        {
            using var services = (ServiceProvider)Services(fixture);
            fixture.ResponseOverride = (_, _, _) => throw new InvalidOperationException("Exact r7 adoption attempted a download.");
            using (var log = new StringWriter())
            {
                var context = new CliContext(services, new CliOutput(log, isJson: true));
                int code = await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
                    { Operation = "apply", Release = baseDescriptor.ReleaseId, Channel = CHANNEL, Install = fixture.Entry.Id.ToString() },
                    cancel.Token);
                fs.File.WriteAllText(At(fs, output, "adopt-r7.cli.jsonl"), log.ToString());
                Assert.That(code, Is.Zero, log.ToString());
            }
            Assert.That(fixture.Requests, Is.Empty);
            Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)?.ReleaseId, Is.EqualTo(baseDescriptor.ReleaseId));
            Write("adopted-r7.json", new { Release = baseDescriptor.ReleaseId, Utc = DateTime.UtcNow, Requests = fixture.Requests.Count });
            long downloaded = 0;
            fixture.ResponseOverride = (url, offset, _) =>
            {
                var uri = new Uri(url);
                string filename = uri.Segments.Last();
                if (!uri.AbsolutePath.StartsWith("/private-skip-r9/", StringComparison.Ordinal) ||
                    !filename.StartsWith(latest.Update.AssetPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected r8, stock or foreign asset request: " + url);
                var response = fixture.Response(url, offset, latest.Artifact, filename[latest.Update.AssetPrefix.Length..]);
                Interlocked.Add(ref downloaded, response.Content.Headers.ContentLength ?? 0);
                return response;
            };
            var timer = Stopwatch.StartNew();
            using (var log = new StringWriter())
            {
                var context = new CliContext(services, new CliOutput(log, isJson: true));
                int code = await new UpdateCommand().RunWithContextAsync(context, new UpdateSettings
                    { Channel = CHANNEL, Install = fixture.Entry.Id.ToString() }, cancel.Token);
                fs.File.WriteAllText(At(fs, output, "skipped-update.cli.jsonl"), log.ToString());
                Assert.That(code, Is.Zero, log.ToString());
            }
            var identity = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id);
            Assert.That(identity?.ReleaseId, Is.EqualTo(latest.Patch.FullSource!.ReleaseId));
            Assert.That(identity?.ManifestSha256, Is.EqualTo(Digest(r9.ToJson())));
            AssertTreeStreaming(fs, install, r9);
            var targetPaths = r9.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in r7.Files.Where(file => !targetPaths.Contains(file.Path)))
                Assert.That(fs.File.Exists(At(fs, install, file.Path)), Is.False, file.Path);
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveSha));
            Assert.That(HashFile(fs, save), Is.EqualTo(saveSha));
            Assert.That(HashFile(fs, mod), Is.EqualTo(modSha));
            Assert.That(downloaded, Is.EqualTo(latest.Update.DownloadBytes));
            Write("skip-result.json", new
            {
                ExactSkippedReleaseUpdate = true, InitialRelease = baseDescriptor.ReleaseId,
                SkippedRelease = intermediate.Patch.FullSource!.ReleaseId, FinalRelease = identity!.ReleaseId,
                TargetFiles = r9.Files.Count, TargetBytes = r9.TotalBytes, Downloads = fixture.Requests.Count,
                DownloadBytes = downloaded, IntermediateOrStockRequests = 0, UserSaveAndModPreserved = true,
                StockArchiveSha256 = archiveSha, Seconds = timer.Elapsed.TotalSeconds, initialFree, minimumFree, FinalFree = drive.AvailableFreeSpace
            });
        }
        finally
        {
            cancel.Cancel();
            await monitor;
        }

        StockBaselineManifest ReadManifest(string path) => StockBaselineManifest.FromJson(fs.File.ReadAllBytes(path));

        void Write(string name, object value)
        {
            using Stream file = fs.FileStream.New(At(fs, output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Restore the retained private Windows player to its exact stock tree through the offline uninstall command.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeRetainedPlayer_OfflineUninstallRestoresCompleteStockAsync()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R9_OFFLINE_UNINSTALL");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the isolated RX8 installation after the exact r9 CLI round trip.");
            return;
        }
        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, "RX8");
        string install = At(fs, root, "game");
        string storage = At(fs, root, "launcher");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, "full-r9-offline-stock-restore-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        foreach (string path in new[] { root, install, storage, output })
            paths.ValidateControlledPath(path);
        Assert.That(fs.File.Exists(At(fs, output, "restore-start.json")), Is.False, "Preserve the previous full restore evidence.");
        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(install)!);
        long initialFree = drive.AvailableFreeSpace;
        Assert.That(initialFree, Is.GreaterThanOrEqualTo(43_000_000_000L));
        using var roundtrip = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, batch, "installed-r9-cli-roundtrip-r1/roundtrip-result.json")));
        Assert.That(roundtrip.RootElement.GetProperty("Success").GetBoolean(), Is.True);
        using var adoption = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, batch, "native-r8-r9-in-place-r1/full-size-legacy-adoption.json")));
        Assert.That(adoption.RootElement.GetProperty("InstallRoot").GetString(), Is.EqualTo(install));
        Assert.That(adoption.RootElement.GetProperty("LauncherStorage").GetString(), Is.EqualTo(storage));
        string archiveSha = adoption.RootElement.GetProperty("StockArchiveSha256").GetString()!;
        Assert.That(roundtrip.RootElement.GetProperty("StockArchiveSha256").GetString(), Is.EqualTo(archiveSha));
        string archive = At(fs, install, "uninstall.zip");
        string save = At(fs, root, "profile/Saves/sparse-update-user-save.bin");
        string mod = At(fs, install, "mods/sparse-update-user-mod.bin");
        string saveSha = HashFile(fs, save);
        string modSha = HashFile(fs, mod);
        var stock = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json")));
        var r9 = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, batch, "r9-integrated-runtime-r1/delta/r9-target-input.json")));
        // Uninstall has no release transport dependency. The fixture fails if any asset is requested.
        var unusedPatch = new ReleasePatch
        {
            Version = VERSION, Requires = new PatchRequirement(), ReleasedAt = DateTime.UtcNow,
            Size = 1, ChecksumSha256 = Digest([0])
        };
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = CHANNEL, GeneratedAt = DateTime.UtcNow, Patches = [unusedPatch] };
        var fixture = new Fixture(fs, root, At(fs, workspace, "RTX1/game"), stock, r9, output, null!,
            initializeStock: false, suppliedFeed: feed, suppliedPatch: unusedPatch);
        fixture.Entry.Id = adoption.RootElement.GetProperty("InstallationId").GetGuid();
        fixture.Config.ActiveKsp2InstallId = fixture.Entry.Id;
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string _) => drive.AvailableFreeSpace);
        fixture.Offline = true;
        fixture.ResponseOverride = (_, _, _) => throw new InvalidOperationException("Offline uninstall attempted a download.");
        var installed = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id);
        Assert.That(installed?.ManifestSha256, Is.EqualTo(Digest(r9.ToJson())));
        var timer = Stopwatch.StartNew();
        Write("restore-start.json", new { Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId, initialFree, archiveSha });
        long minimumFree = initialFree;
        using var cancel = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                minimumFree = Math.Min(minimumFree, drive.AvailableFreeSpace);
                if (minimumFree < 5L * 1024 * 1024 * 1024)
                    cancel.Cancel();
                await Task.Delay(1000);
            }
        });
        try
        {
            using var log = new StringWriter();
            using var services = (ServiceProvider)Services(fixture);
            var context = new CliContext(services, new CliOutput(log, isJson: true));
            int code;
            try
            {
                code = await new UninstallCommand().RunWithContextAsync(context,
                    new UninstallSettings { Install = fixture.Entry.Id.ToString(), AssumeYes = true }, cancel.Token);
            }
            finally
            {
                fs.File.WriteAllText(At(fs, output, "offline-uninstall.cli.jsonl"), log.ToString());
            }
            Assert.That(code, Is.Zero, log.ToString());
            var status = fixture.Coordinator().Reconcile(fixture.Entry.Id);
            Assert.That(status?.Release, Is.Null);
            Assert.That(status?.ArchiveFailure, Is.Null);
            Assert.That(status?.Generation.ToJson(), Is.EqualTo(stock.ToJson()));
            Assert.That(status?.TransactionResult, Is.EqualTo(InstallTransactionResult.Committed));
            AssertTreeStreaming(fs, install, stock);
            foreach (string directory in stock.Directories)
                Assert.That(fs.Directory.Exists(At(fs, install, directory)), Is.True, directory);
            var stockPaths = stock.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in r9.Files.Where(file => !stockPaths.Contains(file.Path)))
                Assert.That(fs.File.Exists(At(fs, install, file.Path)), Is.False, file.Path);
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveSha));
            Assert.That(HashFile(fs, save), Is.EqualTo(saveSha));
            Assert.That(HashFile(fs, mod), Is.EqualTo(modSha));
            Assert.That(fixture.Requests, Is.Empty);
            Write("restore-result.json", new
            {
                ExactOfflineRestore = true, StockFiles = stock.Files.Count, StockBytes = stock.TotalBytes,
                StreamingAssetsFiles = stock.Files.Count(file => file.Path.StartsWith("KSP2_x64_Data/StreamingAssets/", StringComparison.Ordinal)),
                StockArchiveSha256 = archiveSha, UserSaveAndModPreserved = true, Requests = fixture.Requests.Count,
                Seconds = timer.Elapsed.TotalSeconds, initialFree, minimumFree, FinalFree = drive.AvailableFreeSpace
            });
        }
        finally
        {
            cancel.Cancel();
            await monitor;
        }

        void Write(string name, object value)
        {
            using Stream file = fs.FileStream.New(At(fs, output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Use ordinary launcher commands to change the retained private installation's exact player and channel in both directions.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeRetainedPlayer_DowngradeAndUpdateAcrossChannelsAsync()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R9_ROUNDTRIP");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the retained isolated RX8 installation and both complete development update recipes.");
            return;
        }
        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, "RX8");
        string install = At(fs, root, "game");
        string storage = At(fs, root, "launcher");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, "installed-r9-cli-roundtrip-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        foreach (string path in new[] { root, install, storage, output })
            paths.ValidateControlledPath(path);
        Assert.That(fs.File.Exists(At(fs, output, "roundtrip-start.json")), Is.False, "Preserve the existing roundtrip evidence.");
        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(install)!);
        long initialFree = drive.AvailableFreeSpace;
        Assert.That(initialFree, Is.GreaterThanOrEqualTo(16L * 1024 * 1024 * 1024));
        using var adoption = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, batch, "native-r8-r9-in-place-r1/full-size-legacy-adoption.json")));
        Assert.That(adoption.RootElement.GetProperty("InstallRoot").GetString(), Is.EqualTo(install));
        Assert.That(adoption.RootElement.GetProperty("LauncherStorage").GetString(), Is.EqualTo(storage));
        string archiveSha = adoption.RootElement.GetProperty("StockArchiveSha256").GetString()!;
        string archive = At(fs, install, "uninstall.zip");
        Assert.That(HashFile(fs, archive), Is.EqualTo(archiveSha));
        var stock = ReadManifest(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json"));
        string forwardDirectory = At(fs, batch, "r9-integrated-runtime-r1/delta");
        var r8 = ReadManifest(At(fs, batch, "r7-to-r8-exact-copy-recipe-r1/r8-target-input.json"));
        var r9 = ReadManifest(At(fs, forwardDirectory, "r9-target-input.json"));
        const string DOWNGRADE_CHANNEL = "stable";
        const string UPDATE_CHANNEL = "beta";
        const string DOWNGRADE_RELEASE = "private-development-r8";
        const string UPDATE_RELEASE = "private-development-r9";
        var reverse = ReadRetainedPlayerRoute(fs, At(fs, output, "reverse-publication.json"), At(fs, output, "reverse-artifact"),
            stock, r9, r8, DOWNGRADE_RELEASE, DOWNGRADE_CHANNEL, "2.0.0.1.198", DateTime.UtcNow);
        var forward = ReadRetainedPlayerRoute(fs, At(fs, forwardDirectory, "publication-result.json"), At(fs, forwardDirectory, "artifact"),
            stock, r8, r9, UPDATE_RELEASE, UPDATE_CHANNEL, "2.0.0.1.202", DateTime.UtcNow);
        var fixture = new Fixture(fs, root, At(fs, workspace, "RTX1/game"), stock, r8, reverse.Artifact, null!,
            initializeStock: false, suppliedFeed: reverse.Feed, suppliedPatch: reverse.Patch);
        fixture.Entry.Id = adoption.RootElement.GetProperty("InstallationId").GetGuid();
        fixture.Config.ActiveKsp2InstallId = fixture.Entry.Id;
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>()))
            .Returns((string _) => drive.AvailableFreeSpace);
        Assert.That(fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id)?.ManifestSha256, Is.EqualTo(Digest(r9.ToJson())));
        Write("roundtrip-start.json", new { Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId, initialFree, archiveSha });
        string save = At(fs, root, "profile/Saves/sparse-update-user-save.bin");
        string mod = At(fs, install, "mods/sparse-update-user-mod.bin");
        string saveSha = HashFile(fs, save);
        string modSha = HashFile(fs, mod);
        long minimumFree = initialFree;
        using var cancel = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                minimumFree = Math.Min(minimumFree, drive.AvailableFreeSpace);
                if (minimumFree < 5L * 1024 * 1024 * 1024)
                    cancel.Cancel();
                await Task.Delay(1000);
            }
        });
        try
        {
            await Run("downgrade-r8", reverse, r8, r9, false);
            await Run("update-r9", forward, r9, r8, true);
            Write("roundtrip-result.json", new { Success = true, ExactDowngradeAndUpdate = true, ChannelChanges = true,
                UserSaveAndModPreserved = true, StockArchiveSha256 = archiveSha, ProductionAuthenticationProven = false,
                initialFree, minimumFree, FinalFree = drive.AvailableFreeSpace });
        }
        finally
        {
            cancel.Cancel();
            await monitor;
        }

        StockBaselineManifest ReadManifest(string path) => StockBaselineManifest.FromJson(fs.File.ReadAllBytes(path));

        async Task Run(string phase, (string Artifact, FullSourceUpdateArtifact Update, ReleaseManifest Feed, ReleasePatch Patch) route,
            StockBaselineManifest target, StockBaselineManifest previous, bool useUpdate)
        {
            fixture.SuppliedFeed = route.Feed;
            fixture.Patch = route.Patch;
            fixture.Requests.Clear();
            long requestedBytes = 0;
            fixture.ResponseOverride = (url, offset, _) =>
            {
                string filename = new Uri(url).Segments.Last();
                if (!filename.StartsWith(route.Update.AssetPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected stock or other update download: " + url);
                var response = fixture.Response(url, offset, route.Artifact, filename[route.Update.AssetPrefix.Length..]);
                Interlocked.Add(ref requestedBytes, response.Content.Headers.ContentLength ?? 0);
                return response;
            };
            using var log = new StringWriter();
            using var services = (ServiceProvider)Services(fixture);
            var context = new CliContext(services, new CliOutput(log, isJson: true));
            var descriptor = route.Patch.FullSource!;
            TestContext.Progress.WriteLine($"Starting {phase} through the ordinary CLI on the retained fixture.");
            var timer = Stopwatch.StartNew();
            int code;
            try
            {
                code = useUpdate
                    ? await new UpdateCommand().RunWithContextAsync(context, new UpdateSettings
                        { Channel = descriptor.Channel, Install = fixture.Entry.Id.ToString() }, cancel.Token)
                    : await new FullSourceCommand().RunWithContextAsync(context, new FullSourceSettings
                        { Operation = "apply", Channel = descriptor.Channel, Release = descriptor.ReleaseId, Install = fixture.Entry.Id.ToString() },
                        cancel.Token);
            }
            finally
            {
                fs.File.WriteAllText(At(fs, output, phase + ".cli.jsonl"), log.ToString());
            }
            Assert.That(code, Is.Zero, log.ToString());
            var identity = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id);
            Assert.That(identity?.ManifestSha256, Is.EqualTo(descriptor.TargetManifestSha256));
            Assert.That(identity?.Channel, Is.EqualTo(descriptor.Channel));
            AssertTreeStreaming(fs, install, target);
            var paths = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var old in previous.Files.Where(file => !paths.Contains(file.Path)))
                Assert.That(fs.File.Exists(At(fs, install, old.Path)), Is.False, old.Path);
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveSha));
            Assert.That(HashFile(fs, save), Is.EqualTo(saveSha));
            Assert.That(HashFile(fs, mod), Is.EqualTo(modSha));
            Assert.That(fixture.Requests, Is.Not.Empty);
            Write(phase + ".json", new { descriptor.Channel, descriptor.ReleaseId, descriptor.Version, Files = target.Files.Count,
                Bytes = target.TotalBytes, DownloadBytes = requestedBytes, Requests = fixture.Requests.Count, Seconds = timer.Elapsed.TotalSeconds });
        }

        void Write(string name, object value)
        {
            using Stream file = fs.FileStream.New(At(fs, output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static (string Artifact, FullSourceUpdateArtifact Update, ReleaseManifest Feed, ReleasePatch Patch) ReadRetainedPlayerRoute(
        RealFileSystem fs, string receipt, string artifact, StockBaselineManifest stock, StockBaselineManifest source,
        StockBaselineManifest target, string id, string channel, string version, DateTime releasedAt)
    {
        using var document = JsonDocument.Parse(fs.File.ReadAllBytes(receipt));
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(document.RootElement.GetProperty("publication"))!;
        string digest = Digest(source.ToJson());
        string prefix = $"update-{digest.ToLowerInvariant()}-";
        string updateUrl = $"https://fixture.test/{id}/{prefix}release.json";
        var update = new FullSourceUpdateArtifact(digest, publication.ArtifactSha256,
            document.RootElement.GetProperty("markerBytes").GetInt64(), document.RootElement.GetProperty("downloadBytes").GetInt64(),
            updateUrl, updateUrl);
        var recipe = new ReleaseRecipePipeline(fs).Inspect(artifact, publication.ArtifactSha256, publication.TargetManifestSha256);
        ReleaseRecipePipeline.ValidateUpdateFeedArtifact(recipe, target.ToJson(), update);
        // The fixture exposes installed-source recipes and rejects requests for stock delivery.
        var descriptor = new FullSourceReleaseDescriptor(id, channel, version, target.ToJson(), Digest(target.ToJson()),
            $"https://fixture.test/unavailable-stock/{id}/release.json", $"https://fixture.test/unavailable-stock/{id}/release.json",
            [new FullSourceBaselineDefinition(stock.ToJson(), [])], false, 1) { UpdateArtifacts = [update] };
        var patch = new ReleasePatch { Version = version, Requires = new PatchRequirement(), ReleasedAt = releasedAt,
            Size = 1, ChecksumSha256 = Digest([0]), FullSource = descriptor };
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = channel, GeneratedAt = DateTime.UtcNow, Patches = [patch] };
        ReleaseManifestValidator.Validate(feed);
        return (artifact, update, feed, patch);
    }
}
