using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class ProtonLaunchCommandsTest
{
    private static (CliCommandHarness Harness, Ksp2InstallEntry Entry) WithLinuxProfile()
    {
        var harness = new CliCommandHarness();
        harness.OperatingSystem.Setup(platform => platform.IsLinux()).Returns(true);
        string directory = $@"C:\ReduxCliTests\{Guid.NewGuid():N}\KSP2";
        var entry = harness.Installs.AddInstall(harness.CreateGame(directory), "Linux test");
        return (harness, entry);
    }

    [Test]
    public async Task SetProtonLaunch_ReplacesSteamAndPersistsTheChoice()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughSteam = true;

        int exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context,
            new InstallsSetSettings { Install = entry.Name, ProtonLaunch = "on" });

        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Context.ConfigPersisted(config => config.Ksp2Installs.Any(saved =>
            saved.Id == entry.Id && saved.LaunchThroughProton && !saved.LaunchThroughSteam)), Is.True);
        Assert.That(harness.Json.GetProperty("protonLaunch").GetBoolean(), Is.True);
        Assert.That(harness.Json.GetProperty("steamLaunch").GetBoolean(), Is.False);

        harness.Results.GetStringBuilder().Clear();
        await new InstallsShowCommand().RunWithContextAsync(harness.Context,
            new InstallsShowSettings { Install = entry.Name });
        Assert.That(harness.Json.GetProperty("protonLaunch").GetBoolean(), Is.True);
    }

    [Test]
    public async Task SetSteamLaunch_ReplacesProtonAndPersistsTheChoice()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughProton = true;

        int exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context,
            new InstallsSetSettings { Install = entry.Name, SteamLaunch = "on" });

        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Context.ConfigPersisted(config => config.Ksp2Installs.Any(saved =>
            saved.Id == entry.Id && saved.LaunchThroughSteam && !saved.LaunchThroughProton)), Is.True);
    }

    [TestCase("on", "on")]
    [TestCase(null, "sometimes")]
    public async Task InvalidProtonChoice_RejectsTheEntireChange(string? steamLaunch, string protonLaunch)
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchArguments = "-unchanged";

        int exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context,
            new InstallsSetSettings
            {
                Install = entry.Name,
                Arguments = "-changed",
                SteamLaunch = steamLaunch,
                ProtonLaunch = protonLaunch
            });

        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(entry.LaunchArguments, Is.EqualTo("-unchanged"));
        Assert.That(entry.LaunchThroughProton, Is.False);
        Assert.That(entry.LaunchThroughSteam, Is.False);
    }

    [Test]
    public async Task List_ReportsTheConfiguredProtonChoice()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughProton = true;

        await new InstallsCommand().RunWithContextAsync(harness.Context, new InstallsSettings());

        Assert.That(harness.Json.EnumerateArray().Single().GetProperty("protonLaunch").GetBoolean(), Is.True);
    }

    [Test]
    public async Task LinuxWithoutSteamOrProton_ExplainsTheSupportedLaunchChoices()
    {
        var (harness, _) = WithLinuxProfile();

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("--proton-launch"));
        harness.Proton.Verify(runtime => runtime.PrepareAsync(It.IsAny<Action<LogMessage>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task RuntimePreparationFailure_IsReportedWithoutStartingTheGame()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughProton = true;
        entry.LaunchThroughSteam = true;
        harness.Proton.Setup(runtime => runtime.PrepareAsync(It.IsAny<Action<LogMessage>>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Download interrupted"));

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("Download interrupted"));
        harness.Proton.Verify(runtime => runtime.CreateLaunchInfo(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>()), Times.Never);
        harness.Wine.Verify(runtime => runtime.Detect(), Times.Never);
    }

    [Test]
    public async Task CancelledPreparation_DoesNotContinueToLaunch()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughProton = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        harness.Proton.Setup(runtime => runtime.PrepareAsync(It.IsAny<Action<LogMessage>>(), cancellation.Token))
            .Returns(Task.FromCanceled(cancellation.Token));

        await Assert.ThrowsAsync<TaskCanceledException>(() => new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true }, cancellation.Token));

        harness.Proton.Verify(runtime => runtime.CreateLaunchInfo(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task PreparedRuntime_ReceivesTheSelectedInstallAndItsQuotedArguments()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughProton = true;
        entry.LaunchArguments = "-logFile \"a path with spaces.log\"";
        harness.Proton.Setup(runtime => runtime.PrepareAsync(It.IsAny<Action<LogMessage>>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        harness.Proton.Setup(runtime => runtime.CreateLaunchInfo(entry.ExePath,
            harness.FileSystem.Path.GetDirectoryName(entry.ExePath)!, entry.LaunchArguments))
            .Throws(new InvalidOperationException("Launch plan reached"));

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("Launch plan reached"));
        harness.Proton.Verify(runtime => runtime.CreateLaunchInfo(entry.ExePath,
            harness.FileSystem.Path.GetDirectoryName(entry.ExePath)!, entry.LaunchArguments), Times.Once);
    }

    [Test]
    public async Task GameRemovedDuringPreparation_IsRejectedBeforeLaunch()
    {
        var (harness, entry) = WithLinuxProfile();
        entry.LaunchThroughProton = true;
        harness.Proton.Setup(runtime => runtime.PrepareAsync(It.IsAny<Action<LogMessage>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => harness.FileSystem.File.Delete(entry.ExePath))
            .Returns(Task.CompletedTask);

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_NOT_FOUND));
        harness.Proton.Verify(runtime => runtime.CreateLaunchInfo(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task MacWithAProtonFlag_StillUsesTheMacRuntime()
    {
        var (harness, entry) = WithLinuxProfile();
        harness.OperatingSystem.Setup(platform => platform.IsLinux()).Returns(false);
        harness.OperatingSystem.Setup(platform => platform.IsMacOS()).Returns(true);
        entry.LaunchThroughProton = true;

        int exit = await new LaunchCommand().RunWithContextAsync(harness.Context,
            new LaunchSettings { NoBanner = true });

        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        harness.Wine.Verify(runtime => runtime.Detect(), Times.Once);
        harness.Proton.Verify(runtime => runtime.PrepareAsync(It.IsAny<Action<LogMessage>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
