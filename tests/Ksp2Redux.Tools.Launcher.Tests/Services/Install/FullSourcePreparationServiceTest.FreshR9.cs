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
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    /// <summary>
    /// Install a retained source player into a private stock fixture, creating its first stock backup.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeRetainedPlayer_FreshStockInstallCreatesBackupAndPublishesR9Async()
    {
        string? r10Binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R10_FRESH");
        bool latest = !string.IsNullOrEmpty(r10Binding);
        string? coldBinding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R9_COLD_FRESH");
        bool cold = !string.IsNullOrEmpty(coldBinding);
        if (latest && cold)
            throw new InvalidOperationException("Bind one private stock fixture per native fresh-install run.");
        string? binding = latest ? r10Binding : cold ? coldBinding : SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R9_FRESH");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the private warm stock fixture or the private stock fixture and cold local HTTP feed.");
            return;
        }
        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, latest ? "RF10" : cold ? "RC9F" : "RTX1");
        string install = At(fs, root, "game");
        string storage = At(fs, root, "launcher");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, latest ? "full-r10-stock-fresh-r1" : cold ? "full-r9-cold-fresh-r1" : "full-r9-fresh-install-admission-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        foreach (string path in new[] { root, install, storage, output })
            paths.ValidateControlledPath(path);
        Assert.That(fs.File.Exists(At(fs, output, "fresh-start.json")), Is.False, "Preserve previous fresh-install evidence.");
        if (cold)
            Assert.That(fs.Directory.Exists(storage) || fs.File.Exists(storage), Is.False, "A cold install starts without launcher storage.");
        else
            Assert.That(fs.File.Exists(At(fs, output, "warm-cache-ready.json")), Is.True);
        Assert.That(fs.Directory.Exists(At(fs, storage, "full-source-v1")), Is.False);
        string archive = At(fs, install, "uninstall.zip");
        Assert.That(fs.File.Exists(archive), Is.False, "A fresh conversion must create its own stock ZIP.");
        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(install)!);
        long initialFree = drive.AvailableFreeSpace;
        Assert.That(initialFree, Is.GreaterThanOrEqualTo(cold ? 76_000_000_000L : 53_500_000_000L));
        string delivery = At(fs, batch, latest ? "stock-r10-local-publication-r1" : "stock-r9-repair-delivery-r1");
        if (latest)
        {
            using var previous = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, batch, "r10-native-update-r1/update-result.json")));
            Assert.That(previous.RootElement.GetProperty("ExactR9ToR10").GetBoolean(), Is.True);
        }
        else
        {
            using var repaired = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, delivery, "repair-result.json")));
            Assert.That(repaired.RootElement.GetProperty("ExactFullSizeRepair").GetBoolean(), Is.True);
        }
        using var published = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, delivery, "stock-publication.json")));
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(published.RootElement.GetProperty("publication"))!;
        var stock = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json")));
        var target = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, batch, latest
            ? "r9-to-r10-local-publication-r1/r10-target-input.json" : "r9-integrated-runtime-r1/delta/r9-target-input.json")));
        string artifact = At(fs, delivery, "stock-artifact");
        string release = latest ? "private-fresh-r10" : "private-fresh-r9";
        string version = latest ? "2.0.0.1.203" : "2.0.0.1.202";
        var patch = new ReleaseRecipePipeline(fs).CreateFeedEntry(artifact, publication, release, CHANNEL, version,
            "https://fixture.test/fresh/stock/release.json", "https://fixture.test/fresh/stock/release.json",
            [new FullSourceBaselineDefinition(stock.ToJson(), [])], false, DateTime.UtcNow);
        var feed = new ReleaseManifest { SchemaVersion = 3, Channel = CHANNEL, GeneratedAt = DateTime.UtcNow, Patches = [patch] };
        Assert.That(fs.File.Exists(At(fs, install, "mods/user.txt")), Is.False, "Preserve existing unowned files.");
        string keeper = At(fs, workspace, "RX8/game");
        var fixture = new Fixture(fs, root, keeper, stock, target, artifact, publication,
            initializeStock: false, suppliedFeed: feed, suppliedPatch: patch);
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string _) => drive.AvailableFreeSpace);
        using var handler = cold ? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false } : null;
        if (cold)
        {
            using var server = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, output, "server-ready.json")));
            var endpoint = new Uri(server.RootElement.GetProperty("url").GetString()!);
            Assert.That(endpoint.Scheme, Is.EqualTo("http"));
            Assert.That(endpoint.Host, Is.EqualTo("127.0.0.1"));
            var transport = new ManifestReleasesFeedProviderService(new ExecutingAssemblyService(),
                fixture.Configuration.Object, Mock.Of<ILogService>(), handler);
            fixture.Transport.Setup(service => service.DownloadFileAsync(It.IsAny<FeedInfo>(), It.IsAny<string>(),
                    It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((FeedInfo authority, string url, long offset, CancellationToken ct) =>
                {
                    var origin = new Uri(url);
                    if (origin.Scheme != "https" || origin.Host != "fixture.test"
                        || !origin.AbsolutePath.StartsWith("/fresh/stock/", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected cold-install fixture artifact URL.");
                    return transport.DownloadFileAsync(authority, new Uri(endpoint, origin.Segments.Last()).AbsoluteUri, offset, ct);
                });
        }
        else
        {
            fixture.ResponseOverride = (_, _, _) => throw new InvalidOperationException("The warmed exact stock package should require no transport.");
        }
        string mod = At(fs, install, "mods/lifecycle-user-mod.bin");
        string modSha = HashFile(fs, mod);
        string save = At(fs, root, "profile/fresh-qualification-save.bin");
        fs.Directory.CreateDirectory(At(fs, root, "profile"));
        using (Stream file = fs.FileStream.New(save, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            file.Write("private fresh-install save sentinel"u8);
        string saveSha = HashFile(fs, save);
        Write("fresh-start.json", new { Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId, initialFree });
        var timer = Stopwatch.StartNew();
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
                code = await new InstallCommand().RunWithContextAsync(context, new InstallSettings
                    { Channel = CHANNEL, Version = version, Install = fixture.Entry.Id.ToString() }, cancel.Token);
            }
            finally
            {
                fs.File.WriteAllText(At(fs, output, "fresh-install.cli.jsonl"), log.ToString());
            }
            Assert.That(code, Is.Zero, log.ToString());
            var status = fixture.Coordinator().Reconcile(fixture.Entry.Id);
            Assert.That(status?.Release?.ReleaseId, Is.EqualTo(release));
            Assert.That(status?.Release?.ManifestSha256, Is.EqualTo(Digest(target.ToJson())));
            Assert.That(status?.TransactionResult, Is.EqualTo(InstallTransactionResult.Committed));
            AssertTreeStreaming(fs, install, target);
            foreach (string directory in target.Directories)
                Assert.That(fs.Directory.Exists(At(fs, install, directory)), Is.True, directory);
            var targetPaths = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in stock.Files.Where(file => !targetPaths.Contains(file.Path)))
                Assert.That(fs.File.Exists(At(fs, install, file.Path)), Is.False, file.Path);
            await new StockArchive(fs, new ZipFileService(fs)).VerifyAsync(archive, stock, cancel.Token);
            string archiveSha = HashFile(fs, archive);
            AssertTreeStreaming(fs, keeper, stock);
            Assert.That(HashFile(fs, mod), Is.EqualTo(modSha));
            Assert.That(HashFile(fs, save), Is.EqualTo(saveSha));
            int requests = 0;
            if (cold)
            {
                var observed = new List<WireRequest>();
                using var reader = new StreamReader(fs.FileStream.New(At(fs, output, "requests.jsonl"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                while (reader.ReadLine() is { } line)
                    observed.Add(JsonSerializer.Deserialize<WireRequest>(line)!);
                int packCount = published.RootElement.GetProperty("packCount").GetInt32();
                var expected = Enumerable.Range(0, packCount).Select(index => $"pack-{index:D6}.bin").Append("release.json");
                Assert.That(observed.Select(row => row.Name).Distinct(), Is.EquivalentTo(expected));
                Assert.That(observed.Count(row => row.Injected == "truncated"), Is.EqualTo(1));
                Assert.That(observed.Any(row => row.Name == "pack-000000.bin" && row.Offset > 0 && row.Status == 206), Is.True);
                Assert.That(observed.Count(row => row.Injected == "corrupted"), Is.EqualTo(1));
                Assert.That(observed.Count(row => row.Name == "pack-000001.bin" && row.Offset == 0 && row.Status == 200), Is.EqualTo(2));
                requests = observed.Count;
            }
            else
            {
                Assert.That(fixture.Requests, Is.Empty);
            }
            string stages = At(fs, storage, "full-source-preparation-v1/stages");
            Assert.That(fs.Directory.EnumerateFileSystemEntries(stages), Is.Empty);
            Write("fresh-result.json", new
            {
                ExactFreshStockToR9 = !latest, ExactFreshStockToR10 = latest, WarmVerifiedPackCache = !cold,
                CombinedColdHttpDelivery = cold, Requests = requests,
                PublishedDownloadBytes = patch.FullSource!.DownloadBytes, InstalledFiles = target.Files.Count, InstalledBytes = target.TotalBytes,
                OriginalStockFilesVerifiedInNewArchive = stock.Files.Count,
                StreamingAssetsFiles = stock.Files.Count(file => file.Path.StartsWith("KSP2_x64_Data/StreamingAssets/", StringComparison.Ordinal)),
                NewArchiveSha256 = archiveSha, NewArchiveBytes = fs.FileInfo.New(archive).Length, UserSaveAndModPreserved = true,
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
}
