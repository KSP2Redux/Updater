using System.Net;
using System.Text;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

public partial class FullSourceFeedTransportTest
{
    private static FeedInfo LoopbackFeed() => new()
    {
        Repository = "https://github.com/fixture/authority", Filename = "manifest-beta.json",
        Token = LoopbackReleaseServer.TEST_TOKEN
    };

    /// <summary>
    /// Authenticated asset discovery cannot supply another repository, duplicate metadata or a credential-bearing URL.
    /// </summary>
    /// <param name="claim">The malformed asset claim.</param>
    /// <returns>The verification task.</returns>
    [TestCase("repository"), TestCase("query"), TestCase("duplicate")]
    public async Task Delivery_LoopbackAssetClaims_RejectBeforeDownload(string claim)
    {
        await using var server = new LoopbackReleaseServer();
        string location = claim == "repository" ? "https://api.github.com/repos/other/repository/releases/assets/1"
            : "https://api.github.com/repos/fixture/authority/releases/assets/1?credential=owned-test-value";
        byte[] json = Encoding.UTF8.GetBytes(claim == "duplicate" ? "{\"assets\":[],\"assets\":[]}"
            : System.Text.Json.JsonSerializer.Serialize(new { assets = new[] { new { name = "release.json", url = location } } }));
        server.Respond = (_, stream, ct) => LoopbackReleaseServer.ReplyAsync(stream, json, 200, ct);
        using var handler = server.Handler();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Service(handler).DownloadFileAsync(LoopbackFeed(),
                "https://github.com/fixture/authority/releases/download/r1/release.json", 0, CancellationToken.None));
        Assert.That(server.Requests.Count, Is.EqualTo(1));
        Assert.That(server.Requests.Single().Authorized, Is.True);
    }

    /// <summary>
    /// Real TLS redirects strip credentials and do not authenticate unrelated repository requests.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Delivery_LoopbackRedirect_RestrictsCredentialsOnWire()
    {
        await using var server = new LoopbackReleaseServer();
        server.Respond = (request, stream, ct) => LoopbackReleaseServer.ReplyAsync(stream, [],
            request.Host == "api.github.com" ? 302 : 200, ct,
            request.Host == "api.github.com" ? "Location: https://objects.fixture.test/asset\r\n" : "");
        using var handler = server.Handler();
        var provider = Service(handler);
        using var response = await provider.DownloadFileAsync(LoopbackFeed(),
            "https://api.github.com/repos/fixture/authority/releases/assets/1", 0, CancellationToken.None);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var unrelated = await provider.DownloadFileAsync(LoopbackFeed(), "https://publisher.fixture.test/asset", 0, CancellationToken.None);
        Assert.That(server.Requests.Select(request => (request.Host, request.Authorized)), Is.EqualTo(new[]
        {
            ("api.github.com", true), ("objects.fixture.test", false), ("publisher.fixture.test", false)
        }));
    }

    /// <summary>
    /// Cancelling real GitHub asset discovery aborts its HTTP body without issuing a public fallback request.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Delivery_LoopbackAssetDiscovery_CancelsWithoutFallback()
    {
        await using var server = new LoopbackReleaseServer();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Respond = async (_, stream, ct) =>
        {
            await LoopbackReleaseServer.ReplyAsync(stream, [], 200, ct, declaredLength: 100);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        using var handler = server.Handler();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> transfer = Service(handler).DownloadFileAsync(LoopbackFeed(),
            "https://github.com/fixture/authority/releases/download/r1/release.json", 0, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        Assert.That(async () => await transfer, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(server.Requests.Count, Is.EqualTo(1));
        Assert.That(server.Requests.Single().Authorized, Is.True);
    }

    /// <summary>
    /// A cancelled real HTTP transfer retains a partial file and resumes it with an exact verified range.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task Delivery_LoopbackCancellation_ResumesVerifiedPartial()
    {
        await using var server = new LoopbackReleaseServer();
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 8192));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool interrupt = true;
        server.Respond = async (request, stream, ct) =>
        {
            if (interrupt)
            {
                await LoopbackReleaseServer.ReplyAsync(stream, bytes[..4096], 200, ct, declaredLength: bytes.Length);
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }
            int offset = request.Range is null ? 0 : int.Parse(request.Range[6..].TrimEnd('-'));
            await LoopbackReleaseServer.ReplyAsync(stream, bytes[offset..], offset == 0 ? 200 : 206, ct,
                offset == 0 ? "" : $"Content-Range: bytes {offset}-{bytes.Length - 1}/{bytes.Length}\r\n");
        };
        using var handler = server.Handler();
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        var space = new Mock<IDiskSpaceService>();
        space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var downloads = new PatchDownloadService(fs, Service(handler), Mock.Of<ILogService>(), space.Object);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var request = new PatchDownloadRequest(LoopbackFeed(), new ReleasePatch
        {
            ChecksumSha256 = hash, Size = bytes.Length, Url = "https://publisher.fixture.test/pack.bin",
            Version = "2.0.0.1.200", ReleasedAt = DateTime.UtcNow, Requires = new PatchRequirement { Version = null }
        }, @"C:\owned-download");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string> transfer = downloads.EnqueueAll([request], PatchDownloadSource.R2, 1, _ => { }, (_, _) => { }, cancellation.Token).Single();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string partial = $@"C:\owned-download\patches\{hash}.patch.partial";
        for (int attempt = 0; attempt < 100 && (!fs.File.Exists(partial) || fs.FileInfo.New(partial).Length != 4096); attempt++)
        {
            await Task.Delay(10);
        }
        await cancellation.CancelAsync();
        Assert.That(async () => await transfer, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(fs.FileInfo.New(partial).Length, Is.EqualTo(4096));
        interrupt = false;
        string completed = await downloads.EnqueueAll([request], PatchDownloadSource.R2, 1, _ => { }, (_, _) => { }, CancellationToken.None).Single();
        Assert.That(fs.File.ReadAllBytes(completed), Is.EqualTo(bytes));
        Assert.That(server.Requests.Last().Range, Is.EqualTo("bytes=4096-"));
        Assert.That(server.Requests.All(observed => !observed.Authorized), Is.True);
    }
}
