using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class InstallCommandTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";

    private static CliCommandHarness WithInstall()
    {
        var harness = new CliCommandHarness();
        harness.Installs.AddInstall(harness.CreateGame(GAME), "Testing");
        return harness;
    }

    [Test]
    public async Task PatchFile_Missing_FailsBeforeTouchingTheInstall()
    {
        // Arrange
        var harness = WithInstall();

        // Act
        var exit = await new InstallCommand().RunWithContextAsync(harness.Context, new InstallSettings { PatchFile = @"C:\typo.zip" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain(@"C:\typo.zip"));
        harness.InstallPlan.Verify(
            p => p.ApplyToFolder(
                It.IsAny<InstallPlan>(),
                It.IsAny<string>(),
                It.IsAny<Action<LogMessage>>(),
                It.IsAny<Action<long, long>>(),
                It.IsAny<Action<int, int>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
