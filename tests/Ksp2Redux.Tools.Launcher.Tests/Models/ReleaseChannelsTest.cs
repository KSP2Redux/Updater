using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;

namespace Ksp2Redux.Tools.Launcher.Tests.Models;

public class ReleaseChannelsTest
{
    [TestCase("beta", "snapshot")]
    [TestCase("BETA", "snapshot")]
    [TestCase("stable", "stable")]
    [TestCase("internal", "internal")]
    [TestCase(null, "")]
    public void DisplayName_ShowsTheBetaFeedAsSnapshot(string? channel, string expected)
        => Assert.That(ReleaseChannels.DisplayName(channel), Is.EqualTo(expected));

    [TestCase("snapshot", "beta")]
    [TestCase("Snapshot", "beta")]
    [TestCase("beta", "beta")]
    [TestCase("stable", "stable")]
    public void FromDisplayName_MapsSnapshotBackToTheBetaFeed(string channel, string expected)
        => Assert.That(ReleaseChannels.FromDisplayName(channel), Is.EqualTo(expected));

    [Test]
    public void GameVersionViewModel_UnlabelledBetaVersion_IsSuffixedWithSnapshot()
    {
        var version = new GameVersion { Channel = "beta", VersionNumber = new Version(0, 2, 9, 0), BuildNumber = "104711" };

        Assert.That(new GameVersionViewModel(version).VersionString, Is.EqualTo("v0.2.9.0.104711-snapshot"));
    }
}
