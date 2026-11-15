using System.Text.Json;
using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliUpdateNoticeTest
{
    // A repository URL without an owner and name fails before any request is made, which stands in
    // for GitHub being unreachable without the test touching the network.
    [Test]
    public async Task CheckFails_RecordsTheAttemptAndKeepsTheLastKnownVersion()
    {
        // Arrange
        CliCommandHarness harness = new();
        harness.Output = new CliOutput(harness.Results, isJson: false, CliCapabilities.Detect(ColorMode.Always, isJson: false, isVerbose: false));
        harness.Context.ConfigService.Config.LauncherRepo = "https://github.com/KSP2Redux";
        var statePath = harness.FileSystem.Path.Combine(harness.Context.ConfigService.GetLocalStorageDirectory(), "cli-update-check.json");
        harness.FileSystem.File.WriteAllText(statePath, """{"checkedAt":"2026-01-01T00:00:00+00:00","latestVersion":"0.0.0.1"}""");

        // Act
        await CliUpdateNotice.NotifyAsync(harness.Context, isEnabled: true, CancellationToken.None);

        // Assert
        var state = JsonDocument.Parse(harness.FileSystem.File.ReadAllText(statePath)).RootElement;
        Assert.That(state.GetProperty("checkedAt").GetDateTimeOffset(), Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(1)));
        Assert.That(state.GetProperty("latestVersion").GetString(), Is.EqualTo("0.0.0.1"));
    }
}
