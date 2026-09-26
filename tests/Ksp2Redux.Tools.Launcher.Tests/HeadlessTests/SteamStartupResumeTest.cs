using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;
using Ksp2Redux.Tools.Launcher.Services.Steam;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

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

    // The real TryResumeAsync forgets a login Steam has revoked.
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
        TestAppBuilder.SteamSessionService.Setup(s => s.SignOutAsync()).ReturnsAsync(true);

        // Act
        await settings.SignOutOfSteamCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.SteamSessionService.Verify(s => s.SignOutAsync(), Times.Once);
    }

    [AvaloniaTest]
    public async Task SignOut_SteamUnreachable_ExplainsHowToRevokeTheLogin()
    {
        // Arrange
        StartLauncherWithSavedLogin();
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        TestAppBuilder.SteamSessionService.Setup(s => s.SignOutAsync()).ReturnsAsync(false);

        // Act
        await settings.SignOutOfSteamCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Signed Out", SteamSessionService.REVOKE_FAILED_MESSAGE, It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(),
            It.IsAny<Avalonia.Controls.WindowStartupLocation>()), Times.Once);
    }

    // An endless animation over the backdrop blurs re-renders them every frame. The pulsing Steam icon cost about
    // 35% of a CPU core and made typing in the sign-in window lag.
    [AvaloniaTest]
    public void MainWindow_Styles_HaveNoEndlessAnimations()
    {
        // Arrange
        var window = new MainWindow();

        // Act
        var endless = AllStyles(window.Styles)
            .SelectMany(style => style.Animations)
            .OfType<Avalonia.Animation.Animation>()
            .Where(animation => animation.IterationCount == Avalonia.Animation.IterationCount.Infinite)
            .ToList();

        // Assert
        Assert.That(endless, Is.Empty);
    }

    private static IEnumerable<Avalonia.Styling.StyleBase> AllStyles(IEnumerable<Avalonia.Styling.IStyle> styles)
    {
        foreach (var style in styles)
        {
            if (style is Avalonia.Styling.StyleBase styleBase)
            {
                yield return styleBase;
                foreach (var child in AllStyles(styleBase.Children))
                {
                    yield return child;
                }
            }
            else if (style is Avalonia.Styling.Styles group)
            {
                foreach (var child in AllStyles(group))
                {
                    yield return child;
                }
            }
        }
    }
}
