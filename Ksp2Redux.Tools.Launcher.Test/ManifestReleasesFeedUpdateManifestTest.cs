using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

public class ManifestReleasesFeedUpdateManifestTest
{
    private static (ManifestReleasesFeed Feed, Mock<IManifestReleasesFeedProviderService> Provider)
        MakeFeed()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var provider = new Mock<IManifestReleasesFeedProviderService>();
        var log = new Mock<ILogService>();
        var feedInfo = new FeedInfo { Repository = "KSP2Redux/Redux", Filename = "manifest-beta.json" };

        var feed = new ManifestReleasesFeed(fs, provider.Object, log.Object, @"C:\downloads", feedInfo);
        return (feed, provider);
    }

    private static ReleaseManifest MakeManifest(string channel, string version) => new()
    {
        Channel = channel,
        GeneratedAt = new DateTime(2026, 1, 1),
        SchemaVersion = 1,
        Patches = [new ReleasePatch { Version = version, Requires = new PatchRequirement { Version = null }, Size = 1, Url = "https://x", ChecksumSha256 = "0", ReleasedAt = DateTime.UtcNow }],
    };

    [Test]
    public async Task UpdateManifest_SucceedsThenReturnsNull_KeepsThePreviousPatchesAndChannel()
    {
        var (feed, provider) = MakeFeed();
        provider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(MakeManifest("beta", "1.2.3.4.5"));
        Assert.That(await feed.UpdateManifest(), Is.True);

        provider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync((ReleaseManifest?)null);
        var succeeded = await feed.UpdateManifest();

        Assert.Multiple(() =>
        {
            Assert.That(succeeded, Is.False, "A failed refresh should still report failure.");
            Assert.That(feed.CurrentChannel, Is.EqualTo("beta"), "The last known channel must not be wiped out by a failed refresh.");
            Assert.That(feed.GetAllVersions().Select(v => v.BuildNumber), Is.EqualTo(new[] { "5" }),
                "A failed refresh must keep showing the last known patch list, not an empty one.");
        });
    }

    [Test]
    public async Task UpdateManifest_SucceedsThenThrows_KeepsThePreviousPatchesAndChannel()
    {
        var (feed, provider) = MakeFeed();
        provider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(MakeManifest("beta", "1.2.3.4.5"));
        Assert.That(await feed.UpdateManifest(), Is.True);

        provider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ThrowsAsync(new HttpRequestException("network unreachable"));
        var succeeded = await feed.UpdateManifest();

        Assert.Multiple(() =>
        {
            Assert.That(succeeded, Is.False);
            Assert.That(feed.CurrentChannel, Is.EqualTo("beta"));
            Assert.That(feed.GetAllVersions().Select(v => v.BuildNumber), Is.EqualTo(new[] { "5" }));
        });
    }

    [Test]
    public async Task UpdateManifest_NeverSucceededAndFails_FallsBackToAnEmptyInvalidManifest()
    {
        var (feed, provider) = MakeFeed();
        provider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync((ReleaseManifest?)null);

        var succeeded = await feed.UpdateManifest();

        Assert.Multiple(() =>
        {
            Assert.That(succeeded, Is.False);
            Assert.That(feed.CurrentChannel, Is.EqualTo("invalid"));
            Assert.That(feed.GetAllVersions(), Is.Empty);
        });
    }
}
