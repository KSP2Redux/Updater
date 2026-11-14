using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

public class ManifestReleasesFeedUpdateManifestTest
{
    private static (ManifestReleasesFeed Feed, Mock<IManifestReleasesFeedProviderService> Provider)
        MakeFeed()
    {
        var provider = new Mock<IManifestReleasesFeedProviderService>();
        var log = new Mock<ILogService>();
        var feedInfo = new FeedInfo { Repository = "KSP2Redux/Redux", Filename = "manifest-beta.json" };

        var feed = new ManifestReleasesFeed(provider.Object, log.Object, @"C:\downloads", feedInfo);
        return (feed, provider);
    }

    private static ReleaseManifest MakeManifest(string channel, string version) => new()
    {
        Channel = channel,
        GeneratedAt = new DateTime(2026, 1, 1),
        SchemaVersion = 1,
        Patches = [new ReleasePatch { Version = version, Requires = new PatchRequirement { Version = null }, Size = 1, Url = "https://x", ChecksumSha256 = "0", ReleasedAt = DateTime.UtcNow }]
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
            Assert.That(feed.GetAllVersions().Select(v => v.BuildNumber), Is.EqualTo(["5"]),
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
            Assert.That(feed.GetAllVersions().Select(v => v.BuildNumber), Is.EqualTo(["5"]));
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

    [Test]
    public async Task RepairPlanUsesSelectedFeedReleaseAndKeepsTheNormalPlanSeparate()
    {
        var (feed, provider) = MakeFeed();
        var manifest = MakeManifest("beta", "1.2.3.4.6");
        var digest = new string('A', 64);
        manifest.SchemaVersion = 3;
        manifest.Patches[0] = manifest.Patches[0] with
        {
            FullSource = new FullSourceReleaseDescriptor("current-beta", "beta", "1.2.3.4.6", [], digest,
                "https://fixture.test/release.json", "https://fixture-backup.test/release.json", [], false, 1)
        };
        provider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(manifest);
        Assert.That(await feed.UpdateManifest(), Is.True);

        var selected = feed.GetAllVersions().Single();
        var repair = feed.GetFullSourcePlan(selected, repair: true)!;

        Assert.Multiple(() =>
        {
            Assert.That(repair.IsFullSourceRepair, Is.True);
            Assert.That(repair.FullSourceReleaseId, Is.EqualTo("current-beta"));
            Assert.That(repair.FullSourceVersion, Is.EqualTo("1.2.3.4.6"));
            Assert.That(repair.FullSourceManifestSha256, Is.EqualTo(digest));
            Assert.That(feed.GetFullSourcePlan(selected)!.IsFullSourceRepair, Is.False);
            Assert.That(feed.GetFullSourcePlan(new GameVersion
            {
                VersionNumber = new Version(1, 2, 3, 4), BuildNumber = "5", Channel = "beta"
            }, repair: true), Is.Null);
        });
    }
}
