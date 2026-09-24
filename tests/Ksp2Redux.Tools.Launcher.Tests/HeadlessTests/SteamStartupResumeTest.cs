using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

// The launcher used to show "Signed in (not connected)" with a Sign in button until the player
// clicked it, even though the saved login was still good. It now reconnects on its own at startup.
public class SteamStartupResumeTest
{
    private static TaskCompletionSource<bool> StartLauncherWithSavedLogin()
    {
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();

        var resume = new TaskCompletionSource<bool>();
        TestAppBuilder.SteamSessionService.Setup(s => s.SavedAccountName).Returns("ewyboy");
        TestAppBuilder.SteamSessionService.Setup(s => s.HasSavedLogin).Returns(true);
        TestAppBuilder.SteamSessionService.Setup(s => s.TryResumeAsync(It.IsAny<CancellationToken>())).Returns(resume.Task);

        var window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return resume;
    }

    [AvaloniaTest]
    public void Startup_WithSavedLogin_ShowsSignedInAndReconnectsWithoutAClick()
    {
        // Act
        StartLauncherWithSavedLogin();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.IsSteamSignedIn, Is.True);
        Assert.That(settings.SteamStatus, Is.EqualTo("Signed in as ewyboy (connecting...)"));
        TestAppBuilder.SteamSessionService.Verify(s => s.TryResumeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // No network is not a reason to sign the player out: the saved login still works once Steam is back.
    [AvaloniaTest]
    public void Startup_SteamUnreachable_StaysSignedInAndSaysOffline()
    {
        // Arrange
        var resume = StartLauncherWithSavedLogin();

        // Act
        resume.SetResult(false);
        Dispatcher.UIThread.RunJobs();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.IsSteamSignedIn, Is.True);
        Assert.That(settings.SteamStatus, Is.EqualTo("Signed in as ewyboy (offline)"));
    }

    // TryResumeAsync forgets a login Steam has revoked, so the saved account is gone afterwards.
    [AvaloniaTest]
    public void Startup_SavedLoginRevoked_ShowsNotSignedIn()
    {
        // Arrange
        var resume = StartLauncherWithSavedLogin();
        TestAppBuilder.SteamSessionService.Setup(s => s.SavedAccountName).Returns((string?)null);
        TestAppBuilder.SteamSessionService.Setup(s => s.HasSavedLogin).Returns(false);

        // Act
        resume.SetResult(false);
        Dispatcher.UIThread.RunJobs();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.IsSteamSignedIn, Is.False);
        Assert.That(settings.SteamStatus, Is.EqualTo("Not signed in"));
    }

    // The title bar icon pulses amber while the startup reconnect is under way.
    [AvaloniaTest]
    public void Startup_WhileReconnecting_IndicatorShowsConnecting()
    {
        // Act
        StartLauncherWithSavedLogin();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.IsSteamConnecting, Is.True);
        Assert.That(settings.IsSteamConnected, Is.False);
        Assert.That(settings.SteamIndicatorTooltip, Is.EqualTo("Connecting to Steam..."));
    }

    // Clicking the grey icon after a failed reconnect tries again with the saved login, no sign-in window.
    [AvaloniaTest]
    public async Task ClickingIndicator_WhenOffline_ReconnectsWithTheSavedLogin()
    {
        // Arrange
        var resume = StartLauncherWithSavedLogin();
        resume.SetResult(false);
        Dispatcher.UIThread.RunJobs();
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.SteamIndicatorTooltip, Does.EndWith("Click to reconnect."));
        TestAppBuilder.SteamSessionService.Setup(s => s.TryResumeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        await settings.ConnectSteamCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.SteamSessionService.Verify(s => s.TryResumeAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        TestAppBuilder.SteamDialogService.Verify(d => d.ShowSignInAsync(), Times.Never);
    }

    // With no saved login there is nothing to reconnect, so the click opens the sign-in window.
    [AvaloniaTest]
    public async Task ClickingIndicator_WithoutSavedLogin_OpensTheSignInWindow()
    {
        // Arrange
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.SteamDialogService.Setup(d => d.ShowSignInAsync()).ReturnsAsync(false);
        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        // Act
        await settings.ConnectSteamCommand.ExecuteAsync(null);

        // Assert
        Assert.That(settings.SteamIndicatorTooltip, Is.EqualTo("Not signed in to Steam. Click to sign in."));
        TestAppBuilder.SteamDialogService.Verify(d => d.ShowSignInAsync(), Times.Once);
    }

    // Download KSP2 needs a Steam account, so it stays greyed out until one is signed in, and its
    // tooltip says how to sign in rather than what the button does.
    [AvaloniaTest]
    public void DownloadButton_WithoutSteam_IsDisabledAndExplainsWhy()
    {
        // Arrange
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();
        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };

        // Act
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.CanDownloadFromSteam, Is.False);
        Assert.That(settings.DownloadFromSteamTooltip, Does.StartWith("Sign in with Steam to download KSP2."));
    }

    [AvaloniaTest]
    public void DownloadButton_WithSavedSteamLogin_IsEnabled()
    {
        // Act
        StartLauncherWithSavedLogin();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.CanDownloadFromSteam, Is.True);
        Assert.That(settings.DownloadFromSteamTooltip, Does.StartWith("Download your own copy of Kerbal Space Program 2"));
    }

    [AvaloniaTest]
    public async Task SignOutFromTheMenu_SignsOutOfSteam()
    {
        // Arrange
        StartLauncherWithSavedLogin();
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        TestAppBuilder.SteamSessionService.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask);

        // Act
        await settings.SignOutOfSteamCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.SteamSessionService.Verify(s => s.SignOutAsync(), Times.Once);
    }
}
