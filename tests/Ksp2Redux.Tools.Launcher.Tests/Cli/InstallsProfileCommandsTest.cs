using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

// The launcher's Settings tab edits a profile's launch arguments, graphics jobs and Steam launch and can
// uninstall KSP2 itself, and the CLI could do none of it.
public class InstallsProfileCommandsTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";

    private static (CliCommandHarness Harness, Guid Id) WithProfile()
    {
        var harness = new CliCommandHarness();
        var entry = harness.Installs.AddInstall(harness.CreateGame(GAME), "Testing");
        return (harness, entry.Id);
    }

    [Test]
    public async Task Set_LaunchSettings_AreSavedToTheProfile()
    {
        // Arrange
        var (harness, id) = WithProfile();

        // Act
        var exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context, new InstallsSetSettings
        {
            Install = "Testing",
            Arguments = " -popupwindow -screen-width 1920 ",
            GraphicsJobs = "off",
            SteamLaunch = "on",
            SteamAppId = "123",
        });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        var saved = harness.Installs.Entries.Single(e => e.Id == id);
        Assert.That(saved.LaunchArguments, Is.EqualTo("-popupwindow -screen-width 1920"));
        Assert.That(saved.DisableGraphicsJobs, Is.True);
        Assert.That(saved.LaunchThroughSteam, Is.True);
        Assert.That(saved.SteamAppId, Is.EqualTo("123"));
        Assert.That(harness.FileSystem.File.ReadAllText(harness.Context.ConfigService.Config.StoragePath), Does.Contain("-screen-width 1920"));
    }

    [Test]
    public async Task Set_ClearArguments_LeavesNone()
    {
        // Arrange
        var (harness, id) = WithProfile();

        // Act
        await new InstallsSetCommand().RunWithContextAsync(harness.Context, new InstallsSetSettings { Install = "Testing", ClearArguments = true });

        // Assert
        Assert.That(harness.Installs.Entries.Single(e => e.Id == id).LaunchArguments, Is.Empty);
    }

    [TestCase("maybe", null)]
    [TestCase(null, "sometimes")]
    public async Task Set_SwitchThatIsNeitherOnNorOff_ChangesNothing(string? graphicsJobs, string? steamLaunch)
    {
        // Arrange
        var (harness, id) = WithProfile();

        // Act
        var exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context, new InstallsSetSettings
        {
            Install = "Testing",
            Arguments = "-changed",
            GraphicsJobs = graphicsJobs,
            SteamLaunch = steamLaunch,
        });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Installs.Entries.Single(e => e.Id == id).LaunchArguments, Is.EqualTo("-popupwindow"));
    }

    [Test]
    public async Task Set_ArgumentsAndClearArguments_IsAUsageError()
    {
        // Arrange
        var (harness, id) = WithProfile();

        // Act
        var exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context,
            new InstallsSetSettings { Install = "Testing", Arguments = "-x", ClearArguments = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Installs.Entries.Single(e => e.Id == id).LaunchArguments, Is.EqualTo("-popupwindow"));
    }

    [Test]
    public async Task Set_NothingGiven_IsAUsageError()
    {
        // Arrange
        var (harness, _) = WithProfile();

        // Act
        var exit = await new InstallsSetCommand().RunWithContextAsync(harness.Context, new InstallsSetSettings { Install = "Testing" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
    }

    [TestCase("on", true)]
    [TestCase("OFF", false)]
    [TestCase("true", true)]
    [TestCase("0", false)]
    public void TryParseSwitch_OnOrOff_Parses(string value, bool expected)
    {
        // Act
        var ok = InstallsSetCommand.TryParseSwitch(value, out var parsed);

        // Assert
        Assert.That(ok, Is.True);
        Assert.That(parsed, Is.EqualTo(expected));
    }

    [Test]
    public async Task Show_Profile_ReportsEveryLaunchSetting()
    {
        // Arrange
        var (harness, _) = WithProfile();

        // Act
        var exit = await new InstallsShowCommand().RunWithContextAsync(harness.Context, new InstallsShowSettings { Install = "Testing" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        var json = harness.Json;
        Assert.That(json.GetProperty("launchArguments").GetString(), Is.EqualTo("-popupwindow"));
        Assert.That(json.GetProperty("graphicsJobs").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("steamLaunch").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("active").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Delete_Confirmed_DeletesTheGameAndItsProfile()
    {
        // Arrange
        var (harness, id) = WithProfile();

        // Act
        var exit = await new InstallsDeleteCommand().RunWithContextAsync(harness.Context, new InstallsDeleteSettings { Install = "Testing", AssumeYes = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.FileSystem.Directory.Exists(GAME), Is.False);
        Assert.That(harness.Installs.Entries.Any(e => e.Id == id), Is.False);
        Assert.That(harness.Json.GetProperty("deleted").GetBoolean(), Is.True);
    }

    // Deleting 30 GB is not something a script should do by accident.
    [Test]
    public async Task Delete_NoTerminalAndNoYes_DeletesNothing()
    {
        // Arrange
        var (harness, id) = WithProfile();

        // Act
        var exit = await new InstallsDeleteCommand().RunWithContextAsync(harness.Context, new InstallsDeleteSettings { Install = "Testing" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.FileSystem.File.Exists(GAME + @"\KSP2_x64.exe"), Is.True);
        Assert.That(harness.Installs.Entries.Any(e => e.Id == id), Is.True);
    }

    [Test]
    public async Task Delete_NotAGameFolder_DeletesNothing()
    {
        // Arrange
        var (harness, id) = WithProfile();
        harness.FileSystem.Directory.Delete(GAME + @"\KSP2_x64_Data", recursive: true);

        // Act
        var exit = await new InstallsDeleteCommand().RunWithContextAsync(harness.Context, new InstallsDeleteSettings { Install = "Testing", AssumeYes = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_NOT_FOUND));
        Assert.That(harness.FileSystem.File.Exists(GAME + @"\KSP2_x64.exe"), Is.True);
        Assert.That(harness.Installs.Entries.Any(e => e.Id == id), Is.True);
    }

    // Steam cannot run KSP2 on macOS, so a ticked Steam launch from an old config must not win there.
    [Test]
    public async Task Launch_OnMacOSWithSteamLaunchTicked_GoesThroughWineAndSaysWhenThereIsNone()
    {
        // Arrange
        var (harness, id) = WithProfile();
        harness.Installs.Entries.Single(e => e.Id == id).LaunchThroughSteam = true;
        harness.OperatingSystem.Setup(o => o.IsMacOS()).Returns(true);
        harness.Wine.Setup(w => w.Detect()).Returns((WineRuntime?)null);

        // Act
        var exit = await new LaunchCommand().RunWithContextAsync(harness.Context, new LaunchSettings { NoBanner = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.LAUNCH_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("compatibility runtime"));
        harness.Wine.Verify(w => w.Detect(), Times.Once);
    }
}
