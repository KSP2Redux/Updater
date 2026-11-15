using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class UpdateCommandTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";

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
