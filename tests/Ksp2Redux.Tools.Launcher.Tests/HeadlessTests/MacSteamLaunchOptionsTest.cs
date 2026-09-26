using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

// Steam cannot run KSP2 on macOS.
public class MacSteamLaunchOptionsTest
{
    private static SettingsTabViewModel Start(bool isMacOS)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(isMacOS);
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();

        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
    }

    [AvaloniaTest]
    public void SteamLaunchOptions_OnMacOS_AreHidden()
    {
        // Act
        var settings = Start(isMacOS: true);

        // Assert
        Assert.That(settings.HasSelectedInstall, Is.True);
        Assert.That(settings.ShowSteamLaunchOptions, Is.False);
    }

    [AvaloniaTest]
    public void SteamLaunchOptions_OnWindows_AreShown()
    {
        // Act
        var settings = Start(isMacOS: false);

        // Assert
        Assert.That(settings.ShowSteamLaunchOptions, Is.True);
    }

    [AvaloniaTest]
    public async Task LaunchGame_OnMacOSWithSteamLaunchTicked_UsesTheWineRuntime()
    {
        // Arrange
        var settings = Start(isMacOS: true);
        settings.SelectedInstall!.LaunchThroughSteam = true;
        TestAppBuilder.WineRuntimeService.Setup(w => w.Detect()).Returns((WineRuntime?)null);
        var home = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        // Act
        await home.LaunchGameCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.WineRuntimeService.Verify(w => w.Detect(), Times.AtLeastOnce);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Couldn't Launch", It.Is<string>(s => s.Contains("compatibility runtime")),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }

    // The runtime is an Intel build, and macOS only offers to install Rosetta for apps opened from Finder.
    [AvaloniaTest]
    public async Task LaunchGame_OnMacOSWithoutRosetta_SaysHowToInstallItAndStartsNothing()
    {
        // Arrange
        Start(isMacOS: true);
        TestAppBuilder.WineRuntimeService.Setup(w => w.Detect())
            .Returns(new WineRuntime(WineRuntimeKind.Bundled, "Wine", "/runtime/wine/bin/wine", "/runtime", "/prefix"));
        TestAppBuilder.WineRuntimeService.Setup(w => w.IsRosettaInstalled()).Returns(false);
        var home = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        // Act
        await home.LaunchGameCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Rosetta Needed", WineRuntimeService.ROSETTA_MISSING_MESSAGE,
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
        TestAppBuilder.WineRuntimeService.Verify(w => w.PrepareAsync(It.IsAny<WineRuntime>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
