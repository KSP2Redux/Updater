using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class UpdateCommandTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";
    private const string INSTALLED = "0.2.2.0.32914";

    private static ReleasePatch Patch(string version, string? requires) => new()
    {
        Version = version,
        Requires = new PatchRequirement { Version = requires },
        Url = $"https://example.test/{version}.patch",
        Size = 1,
        ChecksumSha256 = new string('0', 64),
        ReleasedAt = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>An install on <see cref="INSTALLED" /> whose channel's feed publishes the given patches.</summary>
    private static CliCommandHarness WithFeed(params ReleasePatch[] patches)
    {
        CliCommandHarness harness = new();
        harness.SetGameVersion(INSTALLED);
        var entry = harness.Installs.AddInstall(harness.CreateGame(GAME), "Testing");
        harness.FeedProvider.Setup(p => p.GetManifest(It.IsAny<FeedInfo>())).ReturnsAsync(new ReleaseManifest
        {
            Channel = entry.ReleaseChannel,
            SchemaVersion = 1,
            GeneratedAt = DateTime.UtcNow,
            Patches = [.. patches],
        });
        return harness;
    }

    [Test]
    public async Task NewerVersion_AppliesAndReportsIt()
    {
        // Arrange
        var harness = WithFeed(Patch("0.2.9.0.2", null));

        // Act
        var exit = await new UpdateCommand().RunWithContextAsync(harness.Context, new UpdateSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("requested").GetString(), Is.EqualTo("0.2.9.0.2"));
        harness.InstallPlan.Verify(InstallCommandTest.Apply, Times.Once);
        Assert.That(harness.Installs.Entries.Single().LastInstalledVersion?.BuildNumber, Is.EqualTo("32914"));
    }

    [Test]
    public async Task AlreadyNewest_ChangesNothing()
    {
        // Arrange
        var harness = WithFeed(Patch(INSTALLED, null));

        // Act
        var exit = await new UpdateCommand().RunWithContextAsync(harness.Context, new UpdateSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("updated").GetBoolean(), Is.False);
        harness.InstallPlan.Verify(InstallCommandTest.Apply, Times.Never);
    }

    [Test]
    public async Task DryRun_DescribesWithoutApplying()
    {
        // Arrange
        var harness = WithFeed(Patch("0.2.9.0.2", null));

        // Act
        var exit = await new UpdateCommand().RunWithContextAsync(harness.Context, new UpdateSettings { IsDryRun = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("dryRun").GetBoolean(), Is.True);
        Assert.That(harness.Json.GetProperty("to").GetString(), Is.EqualTo("0.2.9.0.2"));
        harness.InstallPlan.Verify(InstallCommandTest.Apply, Times.Never);
    }

    [Test]
    public async Task NoRouteToTheNewest_FailsWithoutApplying()
    {
        // Arrange
        var harness = WithFeed(Patch("0.2.9.0.1", "0.2.9.0.2"), Patch("0.2.9.0.2", "0.2.9.0.1"));

        // Act
        var exit = await new UpdateCommand().RunWithContextAsync(harness.Context, new UpdateSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.StartWith("No route"));
        harness.InstallPlan.Verify(InstallCommandTest.Apply, Times.Never);
    }

    // The feed provider is a bare mock, so every configured feed fails to load, which is what an
    // offline machine sees.
    [Test]
    public async Task Offline_ReportsTheFeedAsUnavailable()
    {
        // Arrange
        CliCommandHarness harness = new();
        harness.SetGameVersion("0.2.2.0.32914");
        harness.Installs.AddInstall(harness.CreateGame(GAME), "Testing");

        // Act
        var exit = await new UpdateCommand().RunWithContextAsync(harness.Context, new UpdateSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.FEED_UNAVAILABLE));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.FEED_UNAVAILABLE));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("could not be loaded"));
    }

    [Test]
    public async Task NoMatchingInstall_WritesTheFailureAsJson()
    {
        // Arrange
        CliCommandHarness harness = new();

        // Act
        var exit = await new UpdateCommand().RunWithContextAsync(harness.Context, new UpdateSettings { Install = "Missing" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_NOT_FOUND));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.INSTALL_NOT_FOUND));
    }

    [Test]
    public void FailVersionNotFound_WritesTheFailureAsJson()
    {
        // Arrange
        CliCommandHarness harness = new();

        // Act
        var exit = harness.Context.FailVersionNotFound("103669", "stable");

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.VERSION_NOT_FOUND));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.VERSION_NOT_FOUND));
    }

    [Test]
    public void FailFeedNotConfigured_NothingFailedToLoad_WritesTheFailureAsJson()
    {
        // Arrange
        CliCommandHarness harness = new();

        // Act
        var exit = harness.Context.FailFeedNotConfigured("internal", []);

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.FEED_NOT_CONFIGURED));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.FEED_NOT_CONFIGURED));
    }
}
