using System.Net;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

public class ManifestReleasesFeedDownloadPatchTest
{
    private const string DownloadDir = @"C:\downloads";

    private static (ManifestReleasesFeed Feed, Mock<IManifestReleasesFeedProviderService> Provider, MockFileSystem Fs)
        MakeFeed()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(DownloadDir);
        var provider = new Mock<IManifestReleasesFeedProviderService>();
        var log = new Mock<ILogService>();
        var feedInfo = new FeedInfo { Repository = "KSP2Redux/Redux", Filename = "manifest-beta.json" };

        var feed = new ManifestReleasesFeed(fs, provider.Object, log.Object, DownloadDir, feedInfo);
        return (feed, provider, fs);
    }

    private static ReleasePatch MakePatch(byte[] content, string url = "https://example.com/patch.patch")
    {
        return new ReleasePatch
        {
            Version = "0.2.3.1.1234",
            Requires = new PatchRequirement { Version = null },
            Url = url,
            ChecksumSha256 = Convert.ToHexString(SHA256.HashData(content)),
            Size = content.Length,
            ReleasedAt = DateTime.UtcNow,
        };
    }

    private static void SetupDownload(Mock<IManifestReleasesFeedProviderService> provider, byte[] content)
    {
        provider.Setup(p => p.DownloadPatchAsync(It.IsAny<FeedInfo>(), It.IsAny<ReleasePatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage
            {
                Content = new ByteArrayContent(content) { Headers = { ContentLength = content.Length } },
                StatusCode = HttpStatusCode.OK,
            });
    }

    [Test]
    public async Task DownloadPatch_ChecksumMatches_ReturnsDownloadedFile()
    {
        var (feed, provider, fs) = MakeFeed();
        byte[] content = [1, 2, 3, 4, 5];
        var patch = MakePatch(content);
        SetupDownload(provider, content);

        var path = await feed.DownloadPatch(patch, _ => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(fs.File.Exists(path), Is.True);
        Assert.That(fs.File.ReadAllBytes(path), Is.EqualTo(content));
    }

    [Test]
    public async Task DownloadPatch_ChecksumMismatch_ThrowsAndDeletesCorruptFile()
    {
        var (feed, provider, fs) = MakeFeed();
        byte[] content = [1, 2, 3, 4, 5];
        var patch = MakePatch([9, 9, 9, 9, 9]); // checksum for different bytes than what actually downloads
        SetupDownload(provider, content);

        Assert.That(async () => await feed.DownloadPatch(patch, _ => { }, (_, _) => { }, CancellationToken.None),
            Throws.InvalidOperationException);

        string expectedPath = fs.Path.Combine(DownloadDir, "patch.patch");
        Assert.That(fs.File.Exists(expectedPath), Is.False);
    }

    [Test]
    public async Task DownloadPatch_CachedFileWithWrongHash_RedownloadsInsteadOfTrustingCache()
    {
        var (feed, provider, fs) = MakeFeed();
        byte[] goodContent = [1, 2, 3, 4, 5];
        byte[] staleContent = [9, 9, 9, 9, 9]; // same length as goodContent, but wrong bytes/hash
        var patch = MakePatch(goodContent);

        string cachedPath = fs.Path.Combine(DownloadDir, "patch.patch");
        fs.File.WriteAllBytes(cachedPath, staleContent);
        SetupDownload(provider, goodContent);

        var path = await feed.DownloadPatch(patch, _ => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(fs.File.ReadAllBytes(path), Is.EqualTo(goodContent));
        provider.Verify(p => p.DownloadPatchAsync(It.IsAny<FeedInfo>(), It.IsAny<ReleasePatch>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task DownloadPatch_CachedFileMatchesHash_SkipsRedownload()
    {
        var (feed, provider, fs) = MakeFeed();
        byte[] content = [1, 2, 3, 4, 5];
        var patch = MakePatch(content);

        string cachedPath = fs.Path.Combine(DownloadDir, "patch.patch");
        fs.File.WriteAllBytes(cachedPath, content);
        SetupDownload(provider, content);

        var path = await feed.DownloadPatch(patch, _ => { }, (_, _) => { }, CancellationToken.None);

        Assert.That(path, Is.EqualTo(cachedPath));
        provider.Verify(p => p.DownloadPatchAsync(It.IsAny<FeedInfo>(), It.IsAny<ReleasePatch>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
