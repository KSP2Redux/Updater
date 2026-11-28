using System.Diagnostics;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    /// <summary>
    /// Download the full stock-to-r9 release through the real Windows downloader with a cold cache and bounded storage.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task NativeR9ColdTransport_VerifiesEveryPublishedAssetAndResumesAsync()
    {
        string? binding = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_NATIVE_R9_COLD_TRANSPORT");
        if (string.IsNullOrEmpty(binding))
        {
            Assert.Ignore("Bind the local r9 HTTP fixture and published stock artifact.");
            return;
        }

        var fs = new RealFileSystem();
        string project = TestContext.CurrentContext.TestDirectory;
        while (!fs.Directory.Exists(At(fs, project, "Documentation/Omnipatching")))
            project = fs.Path.GetDirectoryName(project) ?? throw new IOException("The current checkout was not found.");
        string workspace = fs.Path.GetDirectoryName(project)!;
        string batch = At(fs, workspace, ".agents/omnipatching-work/fi31-ordinary-development-flow-r1");
        string output = At(fs, batch, "full-r9-cold-transport-r1");
        Assert.That(fs.Path.GetFullPath(binding), Is.EqualTo(output));
        var paths = new InstallTransactionEngine(fs);
        paths.ValidateControlledPath(output);
        string storage = At(fs, output, "cache");
        paths.ValidateControlledPath(storage);
        Assert.That(fs.Directory.Exists(storage) || fs.File.Exists(storage), Is.False);
        using var server = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, output, "server-ready.json")));
        var endpoint = new Uri(server.RootElement.GetProperty("url").GetString()!);
        Assert.That(endpoint.Scheme, Is.EqualTo("http"));
        Assert.That(endpoint.Host, Is.EqualTo("127.0.0.1"));

        string delivery = At(fs, batch, "stock-r9-repair-delivery-r1");
        using var published = JsonDocument.Parse(fs.File.ReadAllBytes(At(fs, delivery, "stock-publication.json")));
        var publication = JsonSerializer.Deserialize<ReleaseRecipePublication>(published.RootElement.GetProperty("publication"))!;
        var manifest = new ReleaseRecipePipeline(fs).Inspect(At(fs, delivery, "stock-artifact"),
            publication.ArtifactSha256, publication.TargetManifestSha256);
        var assets = new List<(string Name, long Size, string Hash)>
        {
            ("release.json", published.RootElement.GetProperty("markerBytes").GetInt64(), publication.ArtifactSha256)
        };
        assets.AddRange(manifest.Packs.Select((pack, index) => ($"pack-{index:D6}.bin", (long)pack.Length, pack.Sha256)));
        long total = assets.Sum(asset => asset.Size);
        Assert.That(total, Is.EqualTo(published.RootElement.GetProperty("downloadBytes").GetInt64()));

        var drive = fs.DriveInfo.New(fs.Path.GetPathRoot(storage)!);
        long startFree = drive.AvailableFreeSpace;
        Assert.That(startFree, Is.GreaterThan(8L * 1024 * 1024 * 1024));
        var space = new Mock<IDiskSpaceService>();
        space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns((string _) => drive.AvailableFreeSpace);
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        var log = Mock.Of<ILogService>();
        var provider = new ManifestReleasesFeedProviderService(new ExecutingAssemblyService(),
            Mock.Of<ILauncherConfigService>(), log, handler);
        var downloader = new PatchDownloadService(fs, provider, log, space.Object);
        var feed = new FeedInfo();
        long verifiedBytes = 0;
        long maxBatchBytes = 0;
        long minimumFree = startFree;
        var timer = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        string journalPath = At(fs, output, "verified-downloads.jsonl");
        using var journal = new StreamWriter(fs.FileStream.New(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        journal.AutoFlush = true;
        foreach (var group in assets.Chunk(4))
        {
            var requests = group.Select(asset => new PatchDownloadRequest(feed, new ReleasePatch
            {
                Version = "2.0.0.1.202", Requires = new PatchRequirement(), ReleasedAt = DateTime.UtcNow,
                Url = new Uri(endpoint, asset.Name).AbsoluteUri, Size = asset.Size, ChecksumSha256 = asset.Hash
            }, storage)).ToArray();
            string[] completed = await Task.WhenAll(downloader.EnqueueAll(requests, PatchDownloadSource.R2, 4,
                _ => { }, (_, _) => { }, cancellation.Token));
            minimumFree = Math.Min(minimumFree, drive.AvailableFreeSpace);
            maxBatchBytes = Math.Max(maxBatchBytes, group.Sum(asset => asset.Size));
            for (int index = 0; index < group.Length; index++)
            {
                var asset = group[index];
                string expected = At(fs, storage, $"patches/{asset.Hash.ToUpperInvariant()}.patch");
                Assert.That(completed[index], Is.EqualTo(expected));
                paths.ValidateControlledPath(expected);
                Assert.That(fs.FileInfo.New(expected).Length, Is.EqualTo(asset.Size));
                journal.WriteLine(JsonSerializer.Serialize(new { asset.Name, asset.Size, asset.Hash }));
                verifiedBytes += asset.Size;
                fs.File.Delete(expected);
            }
        }

        string requestLog = At(fs, output, "requests.jsonl");
        var requestsSeen = new List<WireRequest>();
        using (var reader = new StreamReader(fs.FileStream.New(requestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            while (reader.ReadLine() is { } line)
                requestsSeen.Add(JsonSerializer.Deserialize<WireRequest>(line)!);
        }
        Assert.That(requestsSeen.Select(row => row.Name).Distinct(), Is.EquivalentTo(assets.Select(asset => asset.Name)));
        Assert.That(requestsSeen.Count(row => row.Injected == "truncated"), Is.EqualTo(1));
        Assert.That(requestsSeen.Any(row => row.Name == "pack-000000.bin" && row.Offset > 0 && row.Status == 206), Is.True);
        Assert.That(requestsSeen.Count(row => row.Injected == "corrupted"), Is.EqualTo(1));
        Assert.That(requestsSeen.Count(row => row.Name == "pack-000001.bin" && row.Offset == 0 && row.Status == 200), Is.EqualTo(2));
        Assert.That(verifiedBytes, Is.EqualTo(total));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(At(fs, storage, "patches")), Is.Empty);

        using Stream receipt = fs.FileStream.New(At(fs, output, "cold-transport-result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(receipt, new
        {
            CompleteColdHttpDeliveryVerified = true, ResumedInterruptedBody = true, RejectedCorruptedPack = true,
            Assets = assets.Count, VerifiedBytes = verifiedBytes, HttpRequests = requestsSeen.Count,
            maxBatchBytes, startFree, minimumFree, Seconds = timer.Elapsed.TotalSeconds,
            HostedAuthenticationProven = false, FreshInstallRunCombinedWithThisTransport = false
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record WireRequest(string Name, long Offset, int Status, string? Injected);
}
