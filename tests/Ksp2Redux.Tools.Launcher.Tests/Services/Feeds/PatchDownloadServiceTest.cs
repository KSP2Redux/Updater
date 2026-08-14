using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

public class PatchDownloadServiceTest
{
    private const string STORAGE = @"C:\cache";

    [Test]
    public async Task EnqueueAll_ChunkedPatchDownloadsSelectedSourceAndReconstructs()
    {
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5];
        byte[] whole = first.Concat(second).ToArray();
        var patch = CreateChunkedPatch(first, second, whole);
        var (service, provider, fileSystem) = MakeService();
        provider.Setup(item => item.DownloadFileAsync(
                It.IsAny<FeedInfo>(), "https://r2.test/0", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(HttpStatusCode.OK, first));
        provider.Setup(item => item.DownloadFileAsync(
                It.IsAny<FeedInfo>(), "https://r2.test/1", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(HttpStatusCode.OK, second));

        var tasks = service.EnqueueAll(
            [new PatchDownloadRequest(new FeedInfo(), patch, STORAGE)],
            PatchDownloadSource.R2, 4, _ => { }, (_, _) => { }, CancellationToken.None);
        string result = await tasks[0];

        Assert.That(fileSystem.File.ReadAllBytes(result), Is.EqualTo(whole));
        Assert.That(fileSystem.Directory.Exists(fileSystem.Path.Combine(
            STORAGE, "chunks", patch.ChecksumSha256)), Is.False);
        provider.VerifyAll();
    }

    [Test]
    public async Task EnqueueAll_PartialLegacyPatchResumesWithMatchingRange()
    {
        byte[] whole = [1, 2, 3, 4, 5];
        string hash = Hash(whole);
        var patch = new ReleasePatch
        {
            Version = "1.0.0.1",
            Requires = new PatchRequirement(),
            Url = "https://custom.test/release.patch",
            ChecksumSha256 = hash,
            Size = whole.Length,
            ReleasedAt = DateTime.UtcNow
        };
        var (service, provider, fileSystem) = MakeService();
        string patchDirectory = fileSystem.Path.Combine(STORAGE, "patches");
        fileSystem.Directory.CreateDirectory(patchDirectory);
        fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(patchDirectory, $"{hash}.patch.partial"), whole[..2]);
        var response = Response(HttpStatusCode.PartialContent, whole[2..]);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, whole.Length - 1, whole.Length);
        provider.Setup(item => item.DownloadFileAsync(
                It.IsAny<FeedInfo>(), patch.Url, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        string result = await service.EnqueueAll(
            [new PatchDownloadRequest(new FeedInfo(), patch, STORAGE)],
            PatchDownloadSource.GitHub, 1, _ => { }, (_, _) => { }, CancellationToken.None)[0];

        Assert.That(fileSystem.File.ReadAllBytes(result), Is.EqualTo(whole));
        provider.VerifyAll();
    }

    [TestCase(PatchDownloadSource.R2)]
    [TestCase(PatchDownloadSource.GitHub)]
    public void EnqueueAll_ChunkFailureAllowsSwitchingToOtherMirror(PatchDownloadSource source)
    {
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5];
        var patch = CreateChunkedPatch(first, second, first.Concat(second).ToArray());
        var (service, provider, _) = MakeService();
        provider.Setup(item => item.DownloadFileAsync(
                It.IsAny<FeedInfo>(), It.IsAny<string>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.NotFound, []));

        var tasks = service.EnqueueAll(
            [new PatchDownloadRequest(new FeedInfo(), patch, STORAGE)],
            source, 1, _ => { }, (_, _) => { }, CancellationToken.None);

        var exception = Assert.ThrowsAsync<PatchDownloadException>(async () => await tasks[0]);
        Assert.That(exception!.DownloadSource, Is.EqualTo(source));
        Assert.That(exception.CanSwitchSource, Is.True);
    }

    [Test]
    public void EnqueueAll_LegacyDownloadFailureDoesNotOfferUnavailableMirror()
    {
        byte[] whole = [1, 2, 3];
        var patch = new ReleasePatch
        {
            Version = "1.0.0.1",
            Requires = new PatchRequirement(),
            Url = "https://github.com/example/release.patch",
            ChecksumSha256 = Hash(whole),
            Size = whole.Length,
            ReleasedAt = DateTime.UtcNow
        };
        var (service, provider, _) = MakeService();
        provider.Setup(item => item.DownloadFileAsync(
                It.IsAny<FeedInfo>(), patch.Url, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(HttpStatusCode.NotFound, []));

        var tasks = service.EnqueueAll(
            [new PatchDownloadRequest(new FeedInfo(), patch, STORAGE)],
            PatchDownloadSource.GitHub, 1, _ => { }, (_, _) => { }, CancellationToken.None);

        var exception = Assert.ThrowsAsync<PatchDownloadException>(async () => await tasks[0]);
        Assert.That(exception!.DownloadSource, Is.EqualTo(PatchDownloadSource.GitHub));
        Assert.That(exception.CanSwitchSource, Is.False);
    }

    private static (PatchDownloadService Service, Mock<IManifestReleasesFeedProviderService> Provider,
        MockFileSystem FileSystem) MakeService()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        fileSystem.Directory.CreateDirectory(STORAGE);
        var provider = new Mock<IManifestReleasesFeedProviderService>();
        var diskSpace = new Mock<IDiskSpaceService>();
        diskSpace.Setup(item => item.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        return (new PatchDownloadService(
            fileSystem, provider.Object, new Mock<ILogService>().Object, diskSpace.Object), provider, fileSystem);
    }

    private static ReleasePatch CreateChunkedPatch(byte[] first, byte[] second, byte[] whole) => new()
    {
        Version = "1.0.0.1",
        Requires = new PatchRequirement(),
        ChecksumSha256 = Hash(whole),
        Size = whole.Length,
        ReleasedAt = DateTime.UtcNow,
        Chunks =
        [
            new ReleasePatchChunk
            {
                Index = 0, Size = first.Length, ChecksumSha256 = Hash(first),
                R2Url = "https://r2.test/0", GitHubUrl = "https://github.test/0"
            },
            new ReleasePatchChunk
            {
                Index = 1, Size = second.Length, ChecksumSha256 = Hash(second),
                R2Url = "https://r2.test/1", GitHubUrl = "https://github.test/1"
            }
        ]
    };

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes) => new(status)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
