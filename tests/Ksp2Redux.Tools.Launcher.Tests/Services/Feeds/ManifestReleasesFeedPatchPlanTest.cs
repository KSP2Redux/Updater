using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

public class ManifestReleasesFeedPatchPlanTest
{
    [Test]
    public async Task SelfDependentDelta_UsesFullPatch()
    {
        var full = Patch(2, null, 100);
        var feed = await MakeFeed(full, Patch(2, 2, 1));

        var plan = feed.GetPatchListToVersion(Version(0), Version(2));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Steps.Select(step => step.Value?.GetType()), Is.EqualTo(new[]
            {
                typeof(InstallPlan.RevertToStockStep), typeof(InstallPlan.PrepatchStep), typeof(InstallPlan.ApplyPatchStep)
            }));
            Assert.That(plan.DownloadRequests.Single().Patch, Is.EqualTo(full));
        });
    }

    [Test]
    public async Task TwoVersionCycle_UsesAcyclicRoute()
    {
        var full = Patch(1, null, 100);
        var delta = Patch(2, 1, 10);
        var feed = await MakeFeed(Patch(1, 2, 1), full, delta);

        var plan = feed.GetPatchListToVersion(Version(0), Version(2));

        Assert.That(Downloads(plan), Is.EqualTo(new[] { full, delta }));
    }

    [Test]
    public async Task CycleWithoutFullPatch_ReportsNoRoute()
    {
        var feed = await MakeFeed(Patch(1, 2, 1), Patch(2, 1, 1));

        Assert.That(feed.GetPatchListToVersion(Version(0), Version(2)).Steps, Is.Empty);
    }

    [Test]
    public async Task SharedPrerequisite_RemainsAvailableToCheaperAlternative()
    {
        var full = Patch(1, null, 5);
        var cheapMiddle = Patch(3, 1, 1);
        var cheapEnd = Patch(4, 3, 10);
        var feed = await MakeFeed(full, Patch(2, 1, 20), cheapMiddle, Patch(4, 2, 20), cheapEnd);

        var plan = feed.GetPatchListToVersion(Version(0), Version(4));

        Assert.Multiple(() =>
        {
            Assert.That(Downloads(plan), Is.EqualTo(new[] { full, cheapMiddle, cheapEnd }));
            Assert.That(plan.Cost, Is.EqualTo(16));
        });
    }

    [Test]
    public async Task InstalledPrerequisite_DoesNotNeedItsCyclicHistory()
    {
        var delta = Patch(2, 1, 10);
        var feed = await MakeFeed(Patch(1, 1, 1), delta);

        var plan = feed.GetPatchListToVersion(Version(1), Version(2));

        Assert.Multiple(() =>
        {
            Assert.That(Downloads(plan), Is.EqualTo(new[] { delta }));
            Assert.That(plan.Steps, Has.Count.EqualTo(1));
        });
    }

    private static IEnumerable<ReleasePatch> Downloads(InstallPlan plan) => plan.DownloadRequests.Select(request => request.Patch);

    private static GameVersion Version(int build) => new()
    {
        VersionNumber = new Version(0, 2, 9, 0),
        BuildNumber = build.ToString()
    };

    private static ReleasePatch Patch(int build, int? previous, long size) => new()
    {
        Version = $"0.2.9.0.{build}",
        Requires = new PatchRequirement { Version = previous.HasValue ? $"0.2.9.0.{previous}" : null },
        Url = $"https://example.test/{build}-from-{previous}.patch",
        Size = size,
        ChecksumSha256 = new string('0', 64),
        ReleasedAt = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc)
    };

    private static async Task<ManifestReleasesFeed> MakeFeed(params ReleasePatch[] patches)
    {
        var provider = new Mock<IManifestReleasesFeedProviderService>();
        provider.Setup(service => service.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(new ReleaseManifest
        {
            Channel = "internal",
            SchemaVersion = 1,
            GeneratedAt = DateTime.UtcNow,
            Patches = patches.ToList()
        });
        var feed = new ManifestReleasesFeed(provider.Object, Mock.Of<ILogService>(),
            "downloads", new FeedInfo { Repository = "example/test", Filename = "manifest.json" });
        Assert.That(await feed.UpdateManifest(), Is.True);
        return feed;
    }
}
