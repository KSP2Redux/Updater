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
    /// Apply the exact r10 update to the retained cold r9 Windows installation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeRetainedPlayer_ColdR9UpdatesToExactR10Async()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R10_UPDATE");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the rehydrated cold r9 fixture and local r10 publication.");
            return;
        }

        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string root = At(fs, workspace, "RC9F");
        string install = At(fs, root, "game");
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, "r10-native-update-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        foreach (string path in new[] { root, install, output })
            paths.ValidateControlledPath(path);
        Assert.That(fs.File.Exists(At(fs, output, "update-start.json")), Is.False, "Preserve previous update evidence.");
        using var rehydrated = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, output, "fixture-ready.json")));
        Assert.That(rehydrated.RootElement.GetProperty("fixture").GetString(), Is.EqualTo(root));
        Assert.That(rehydrated.RootElement.GetProperty("filesVerified").GetInt32(), Is.EqualTo(7507));
        Assert.That(rehydrated.RootElement.GetProperty("originalFilePermissionsPreserved").GetBoolean(), Is.True);

        string publicationRoot = At(fs, batch, "r9-to-r10-local-publication-r1");
        var stock = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, project, "reference/omnipatching/baseline/stock-archive-contract.json")));
        var r9 = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, batch, "r9-integrated-runtime-r1/delta/r9-target-input.json")));
        var r10 = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(At(fs, publicationRoot, "r10-target-input.json")));
        const string PREVIOUS = "private-fresh-r9";
        const string TARGET = "private-r10";
        var now = DateTime.UtcNow;
        var next = ReadRetainedPlayerRoute(fs, At(fs, publicationRoot, "r9-to-r10-publication.json"),
            At(fs, publicationRoot, "r9-to-r10-artifact"), stock, r9, r10, TARGET, CHANNEL, "2.0.0.1.203", now);
        string stockPublication = At(fs, batch, "stock-r9-repair-delivery-r1");
        using var stockReceipt = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, stockPublication, "stock-publication.json")));
        var previousArtifact = JsonSerializer.Deserialize<ReleaseRecipePublication>(stockReceipt.RootElement.GetProperty("publication"))!;
        var previous = new ReleaseRecipePipeline(fs).CreateFeedEntry(At(fs, stockPublication, "stock-artifact"), previousArtifact,
            PREVIOUS, CHANNEL, "2.0.0.1.202", "https://fixture.test/unavailable-stock/release.json",
            "https://fixture.test/unavailable-stock/release.json", [new FullSourceBaselineDefinition(stock.ToJson(), [])],
            false, now.AddMinutes(-1));
        var feed = next.Feed with { Patches = [previous, next.Patch] };
        ReleaseManifestValidator.Validate(feed);

        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(install)!);
        long initialFree = drive.AvailableFreeSpace;
        Assert.That(initialFree, Is.GreaterThanOrEqualTo(26L * 1024 * 1024 * 1024));
        var fixture = new Fixture(fs, root, At(fs, workspace, "RX8/game"), stock, r10, next.Artifact, null!,
            initializeStock: false, suppliedFeed: feed, suppliedPatch: next.Patch, seedUserFile: false);
        fixture.Entry.Id = Guid.ParseExact(rehydrated.RootElement.GetProperty("installationId").GetString()!, "N");
        fixture.Config.ActiveKsp2InstallId = fixture.Entry.Id;
        fixture.Space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string _) => drive.AvailableFreeSpace);
        var before = fixture.Coordinator().GetPublishedIdentity(fixture.Entry.Id);
        Assert.That(before?.ReleaseId, Is.EqualTo(PREVIOUS));
        Assert.That(before?.ManifestSha256, Is.EqualTo(Digest(r9.ToJson())));
        var sentinels = new[] { At(fs, root, "profile/fresh-qualification-save.bin"),
            At(fs, install, "mods/lifecycle-user-mod.bin"), At(fs, install, "mods/user.txt") };
        var sentinelHashes = sentinels.ToDictionary(path => path, path => HashFile(fs, path));
        string archive = At(fs, install, "uninstall.zip");
        string archiveHash = rehydrated.RootElement.GetProperty("originalStockZipSha256").GetString()!;
        using var server = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, output, "server-ready.json")));
        var endpoint = new Uri(server.RootElement.GetProperty("url").GetString()!);
        Assert.That(endpoint.Scheme, Is.EqualTo("http"));
        Assert.That(endpoint.Host, Is.EqualTo("127.0.0.1"));
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        var transport = new ManifestReleasesFeedProviderService(new ExecutingAssemblyService(),
            fixture.Configuration.Object, Mock.Of<ILogService>(), handler);
        fixture.Transport.Setup(service => service.DownloadFileAsync(It.IsAny<FeedInfo>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((FeedInfo authority, string url, long offset, CancellationToken ct) =>
            {
                var origin = new Uri(url);
                string filename = origin.Segments.Last();
                if (origin.Scheme != "https" || origin.Host != "fixture.test"
                    || !origin.AbsolutePath.StartsWith("/private-r10/", StringComparison.Ordinal)
                    || !filename.StartsWith(next.Update.AssetPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("The r10 update requested a stock or foreign artifact.");
                return transport.DownloadFileAsync(authority,
                    new Uri(endpoint, filename[next.Update.AssetPrefix.Length..]).AbsoluteUri, offset, ct);
            });

        Write("update-start.json", new { Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId, initialFree });
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
                code = await new UpdateCommand().RunWithContextAsync(context,
                    new UpdateSettings { Channel = CHANNEL, Install = fixture.Entry.Id.ToString() }, cancel.Token);
            }
            finally
            {
                fs.File.WriteAllText(At(fs, output, "update.cli.jsonl"), log.ToString());
            }
            Assert.That(code, Is.Zero, log.ToString());
            var status = fixture.Coordinator().Reconcile(fixture.Entry.Id);
            Assert.That(status?.Release?.ReleaseId, Is.EqualTo(TARGET));
            Assert.That(status?.Release?.ManifestSha256, Is.EqualTo(Digest(r10.ToJson())));
            Assert.That(status?.TransactionResult, Is.EqualTo(InstallTransactionResult.Committed));
            AssertTreeStreaming(fs, install, r10);
            Assert.That(HashFile(fs, archive), Is.EqualTo(archiveHash));
            foreach (var (path, hash) in sentinelHashes)
                Assert.That(HashFile(fs, path), Is.EqualTo(hash), path);
            var requests = new List<UpdateWireRequest>();
            using (var reader = new StreamReader(fs.FileStream.New(At(fs, output, "requests.jsonl"),
                       FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                while (reader.ReadLine() is { } line)
                    requests.Add(JsonSerializer.Deserialize<UpdateWireRequest>(line)!);
            }
            Assert.That(requests.Select(row => row.Name), Is.EquivalentTo(new[] { "release.json", "pack-000000.bin" }));
            Assert.That(requests.Sum(row => row.Bytes), Is.EqualTo(next.Update.DownloadBytes));
            Write("update-result.json", new
            {
                ExactR9ToR10 = true, OriginalRelease = PREVIOUS, InstalledRelease = TARGET,
                InstalledFiles = r10.Files.Count, InstalledBytes = r10.TotalBytes,
                DownloadedBytes = requests.Sum(row => row.Bytes), Requests = requests.Count,
                StockArchiveSha256 = archiveHash, UserFilesPreserved = sentinels.Length,
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

    private sealed record UpdateWireRequest(string Name, long Bytes);
}
