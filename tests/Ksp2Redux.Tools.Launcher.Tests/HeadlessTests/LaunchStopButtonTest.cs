using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class LaunchStopButtonTest
{
    private TaskCompletionSource _gameExited = null!;
    private MainWindow _window = null!;

    private Button SidebarButton(string name) =>
        _window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private HomeTabViewModel Start(bool gameAppears = true)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        _gameExited = new TaskCompletionSource();
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForStartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(gameAppears);
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => _gameExited.Task.WaitAsync(token));
        TestAppBuilder.GameProcessService.Setup(g => g.StopAsync())
            .Returns(Task.CompletedTask)
            .Callback(() => _gameExited.TrySetResult());

        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };
        _window = window;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    public async Task DirectLaunch_WhileTheGameRuns_ShowsAnEnabledStopButton()
    {
        var home = Start();

        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        Assert.Multiple(() =>
        {
            Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
            Assert.That(home.MainButtonEnabled, Is.True);
            Assert.That(SidebarButton("CancelButton").IsVisible, Is.True, "The cancel image button should replace Launch.");
            Assert.That(SidebarButton("CancelButton").IsEffectivelyEnabled, Is.True);
            Assert.That(SidebarButton("LaunchButton").IsVisible, Is.False);
        });
        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.Is<ProcessStartInfo>(s => s.FileName.EndsWith("KSP2_x64.exe"))), Times.Once);

        _gameExited.SetResult();
        await launching;
        Pump();

        Assert.Multiple(() =>
        {
            Assert.That(home.MainButtonShown, Is.Not.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
            Assert.That(SidebarButton("CancelButton").IsVisible, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task SteamLaunch_WhileTheGameRuns_ShowsTheStopButtonInsteadOfLaunch()
    {
        var home = Start();
        TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>().ActiveEntry!.LaunchThroughSteam = true;

        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.Is<ProcessStartInfo>(s =>
            s.FileName == "steam://rungameid/954850" && s.UseShellExecute)), Times.Once);

        _gameExited.SetResult();
        await launching;
    }

    [AvaloniaTest]
    public async Task ClickingStop_Confirmed_StopsTheGameAndReturnsToLaunch()
    {
        var home = Start();
        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        home.CancelCurrentMainButtonActionCommand.Execute(null);
        Pump();
        await launching;

        TestAppBuilder.GameProcessService.Verify(g => g.StopAsync(), Times.Once);
        Assert.That(home.MainButtonShown, Is.Not.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
    }

    [AvaloniaTest]
    public async Task ClickingStop_Declined_LeavesTheGameRunning()
    {
        var home = Start();
        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                "Stop KSP2", It.IsAny<string>(), ButtonEnum.YesNo, It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()))
            .ReturnsAsync(ButtonResult.No);
        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        home.CancelCurrentMainButtonActionCommand.Execute(null);
        Pump();

        TestAppBuilder.GameProcessService.Verify(g => g.StopAsync(), Times.Never);
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));

        _gameExited.SetResult();
        await launching;
    }

    [AvaloniaTest]
    public async Task StopFails_ShowsAnErrorAndKeepsTheStopButtonUsable()
    {
        var home = Start();
        TestAppBuilder.GameProcessService.Setup(g => g.StopAsync()).ThrowsAsync(new InvalidOperationException("Access is denied."));
        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        home.CancelCurrentMainButtonActionCommand.Execute(null);
        Pump();

        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Couldn't Stop KSP2", It.Is<string>(s => s.Contains("Access is denied.")),
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Once);
        Assert.Multiple(() =>
        {
            Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
            Assert.That(home.MainButtonEnabled, Is.True);
        });

        _gameExited.SetResult();
        await launching;
    }

    [AvaloniaTest]
    public async Task ButtonRefreshWhileTheGameRuns_KeepsTheStopButton()
    {
        var home = Start();
        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        home.RefreshMainButtonState();

        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));

        _gameExited.SetResult();
        await launching;
    }

    [AvaloniaTest]
    public async Task GameNeverAppears_ButtonGoesBackToLaunch()
    {
        var home = Start(gameAppears: false);

        await home.LaunchGameCommand.ExecuteAsync(null);

        Assert.That(home.MainButtonShown, Is.Not.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
        TestAppBuilder.GameProcessService.Verify(g => g.WaitForExitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [AvaloniaTest]
    public async Task StartFails_ShowsCouldntLaunchAndNeverShowsStop()
    {
        var home = Start();
        TestAppBuilder.GameProcessService.Setup(g => g.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new InvalidOperationException("blocked by antivirus"));

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Couldn't Launch", It.Is<string>(s => s.Contains("blocked by antivirus")),
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Once);
        Assert.That(home.MainButtonShown, Is.Not.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
        TestAppBuilder.GameProcessService.Verify(g => g.WaitForStartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
