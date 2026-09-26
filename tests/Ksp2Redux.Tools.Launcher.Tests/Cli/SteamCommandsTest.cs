using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class SteamCommandsTest
{
    private const string TARGET = CliCommandHarness.HOME + @"\Games\Kerbal Space Program 2";

    private static CliCommandHarness SignedIn()
    {
        var harness = new CliCommandHarness();
        harness.Steam.Setup(s => s.SavedAccountName).Returns("ewyboy");
        harness.Steam.Setup(s => s.HasSavedLogin).Returns(true);
        harness.Steam.Setup(s => s.TryResumeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        harness.Steam.Setup(s => s.Account).Returns(new SteamAccount("ewyboy", "Ewy"));
        harness.Downloader.Setup(d => d.GetDepotsAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SteamDepot(954851, 1, 33_000_000_000)]);
        return harness;
    }

    // Writes the game files where the real download would.
    private static void DownloadWritesTheGame(CliCommandHarness harness) =>
        harness.Downloader
            .Setup(d => d.DownloadAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((uint _, string folder, IProgress<SteamDownloadProgress> progress, CancellationToken _) =>
            {
                harness.CreateGame(folder);
                progress.Report(new SteamDownloadProgress(10, 10, 1, 1, "Finished"));
                return Task.CompletedTask;
            });

    [Test]
    public async Task Status_NoSavedLogin_SaysHowToSignIn()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new SteamStatusCommand().RunWithContextAsync(harness.Context, new SteamStatusSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.STEAM_NOT_SIGNED_IN));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("steam login"));
    }

    [Test]
    public async Task Status_SavedLogin_ReportsTheAccountWithoutConnecting()
    {
        // Arrange
        var harness = SignedIn();

        // Act
        var exit = await new SteamStatusCommand().RunWithContextAsync(harness.Context, new SteamStatusSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("account").GetString(), Is.EqualTo("ewyboy"));
        harness.Steam.Verify(s => s.TryResumeAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task StatusCheck_SteamUnreachable_IsNotReportedAsSignedOut()
    {
        // Arrange
        var harness = SignedIn();
        harness.Steam.Setup(s => s.TryResumeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var exit = await new SteamStatusCommand().RunWithContextAsync(harness.Context, new SteamStatusSettings { ShouldCheck = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.STEAM_FAILED));
    }

    // The real TryResumeAsync forgets a login Steam has revoked.
    [Test]
    public async Task StatusCheck_LoginRevoked_IsReportedAsSignedOut()
    {
        // Arrange
        var harness = SignedIn();
        harness.Steam.Setup(s => s.TryResumeAsync(It.IsAny<CancellationToken>()))
            .Callback(() => harness.Steam.Setup(s => s.HasSavedLogin).Returns(false))
            .ReturnsAsync(false);

        // Act
        var exit = await new SteamStatusCommand().RunWithContextAsync(harness.Context, new SteamStatusSettings { ShouldCheck = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.STEAM_NOT_SIGNED_IN));
    }

    [Test]
    public async Task Login_WithoutATerminal_RefusesRatherThanHanging()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new SteamLoginCommand().RunWithContextAsync(harness.Context, new SteamLoginSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        harness.Steam.Verify(s => s.SignInWithQrAsync(It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Logout_SignedIn_ForgetsTheLogin()
    {
        // Arrange
        var harness = SignedIn();
        harness.Steam.Setup(s => s.SignOutAsync()).ReturnsAsync(true);

        // Act
        var exit = await new SteamLogoutCommand().RunWithContextAsync(harness.Context, new SteamLogoutSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        harness.Steam.Verify(s => s.SignOutAsync(), Times.Once);
    }

    [Test]
    public async Task Logout_SteamUnreachable_SaysHowToRevokeTheLogin()
    {
        // Arrange
        var harness = SignedIn();
        harness.Steam.Setup(s => s.SignOutAsync()).ReturnsAsync(false);

        // Act
        var exit = await new SteamLogoutCommand().RunWithContextAsync(harness.Context, new SteamLogoutSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("revoked").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Doctor_SignedIn_DoesNotRevealTheLoginName()
    {
        // Arrange
        var harness = SignedIn();
        harness.Steam.Setup(s => s.HasSavedLogin).Returns(true);

        // Act
        await new DoctorCommand().RunWithContextAsync(harness.Context, new DoctorSettings { IsOffline = true });

        // Assert
        Assert.That(harness.Json.GetProperty("steamSignedIn").GetBoolean(), Is.True);
        Assert.That(harness.Results.ToString(), Does.Not.Contain("ewyboy"));
    }

    [Test]
    public async Task Download_NotSignedIn_DownloadsNothing()
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var exit = await new SteamDownloadCommand().RunWithContextAsync(harness.Context, new SteamDownloadSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.STEAM_NOT_SIGNED_IN));
        harness.Downloader.Verify(d => d.DownloadAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Download_NoTerminalAndNoYes_DownloadsNothing()
    {
        // Arrange
        var harness = SignedIn();
        DownloadWritesTheGame(harness);

        // Act
        var exit = await new SteamDownloadCommand().RunWithContextAsync(harness.Context, new SteamDownloadSettings());

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        harness.Downloader.Verify(d => d.DownloadAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.That(harness.Installs.Entries, Is.Empty);
    }

    [Test]
    public async Task Download_Finished_AddsTheGameAsTheActiveProfile()
    {
        // Arrange
        var harness = SignedIn();
        DownloadWritesTheGame(harness);

        // Act
        var exit = await new SteamDownloadCommand().RunWithContextAsync(harness.Context, new SteamDownloadSettings { Name = "Mac copy", AssumeYes = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        var active = harness.Installs.ActiveEntry!;
        Assert.That(active.Name, Is.EqualTo("Mac copy"));
        Assert.That(active.ExePath, Is.EqualTo(TARGET + @"\KSP2_x64.exe"));
        Assert.That(harness.Json.GetProperty("added").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Download_IntoAnExistingProfile_ReusesItAndMakesItActive()
    {
        // Arrange
        var harness = SignedIn();
        DownloadWritesTheGame(harness);
        var other = harness.Installs.AddInstall(harness.CreateGame(@"C:\Games\Other"), "Other");
        var existing = harness.Installs.AddInstall(harness.CreateGame(TARGET), "Existing");
        harness.Installs.SetActiveInstall(other.Id);

        // Act
        await new SteamDownloadCommand().RunWithContextAsync(harness.Context, new SteamDownloadSettings { AssumeYes = true });

        // Assert
        Assert.That(harness.Installs.Entries, Has.Count.EqualTo(2));
        Assert.That(harness.Installs.ActiveEntry!.Id, Is.EqualTo(existing.Id));
        Assert.That(harness.Json.GetProperty("added").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Download_Interrupted_SaysRunningItAgainResumes()
    {
        // Arrange
        var harness = SignedIn();
        harness.Downloader
            .Setup(d => d.DownloadAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<IProgress<SteamDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var exit = await new SteamDownloadCommand().RunWithContextAsync(harness.Context, new SteamDownloadSettings { AssumeYes = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.CANCELLED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("again"));
        Assert.That(harness.Installs.Entries, Is.Empty);
    }

    [TestCase(null, TARGET)]
    [TestCase(@"D:\SteamGames", @"D:\SteamGames\Kerbal Space Program 2")]
    [TestCase(@"D:\SteamGames\Kerbal Space Program 2", @"D:\SteamGames\Kerbal Space Program 2")]
    [TestCase(@"~\Stuff", CliCommandHarness.HOME + @"\Stuff\Kerbal Space Program 2")]
    public void ResolveTarget_AnyFolder_EndsInAKsp2Folder(string? folder, string expected)
    {
        // Arrange
        var harness = new CliCommandHarness();

        // Act
        var target = SteamDownloadCommand.ResolveTarget(harness.Context, folder);

        // Assert
        Assert.That(target, Is.EqualTo(expected));
    }
}
