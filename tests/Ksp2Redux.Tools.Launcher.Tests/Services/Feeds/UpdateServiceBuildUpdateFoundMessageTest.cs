using Ksp2Redux.Tools.Launcher.Services.Feeds;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Feeds;

public class UpdateServiceBuildUpdateFoundMessageTest
{
    [Test]
    public void WithReleaseNotes_IncludesThemInTheMessage()
    {
        var message = UpdateService.BuildUpdateFoundMessage(new Version(0, 2, 0), "- Fixed the crash on startup\n- Faster updates");

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("What's new in v0.2.0:"));
            Assert.That(message, Does.Contain("Fixed the crash on startup"));
            Assert.That(message, Does.Contain("download and update"));
        });
    }

    [Test]
    public void NoReleaseNotes_FallsBackToAPlaceholderInsteadOfBlankSpace()
    {
        var message = UpdateService.BuildUpdateFoundMessage(new Version(0, 2, 0), null);

        Assert.That(message, Does.Contain("No release notes provided"));
    }

    [Test]
    public void VeryLongReleaseNotes_TruncatesInsteadOfShowingAWallOfText()
    {
        var longNotes = new string('a', 900);

        var message = UpdateService.BuildUpdateFoundMessage(new Version(0, 2, 0), longNotes);

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain(new string('a', 500) + "..."));
            Assert.That(message, Does.Not.Contain(new string('a', 501)));
        });
    }
}
