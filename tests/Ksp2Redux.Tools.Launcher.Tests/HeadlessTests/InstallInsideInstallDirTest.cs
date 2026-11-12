using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class InstallInsideInstallDirTest
{
    private const string InstallDir = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2";

    [AvaloniaTest]
    public async Task Install_LauncherRunningFromInsideInstallDir_WarnsAndDoesNotStart()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        // The launcher's own executable sits inside the KSP2 install folder, so an install plan that
        // rewrites the launcher's files would fail against the running process.
        TestAppBuilder.EnvironmentProvider.ProcessPath = TestAppBuilder.FileSystem.Path.Combine(InstallDir, "redux-launcher.exe");

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        // Act
        await homeTabViewModel.InstallReduxCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Can't Install Here", It.IsAny<string>(),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
        // The guard returns before any install UI is shown, so the plan never ran.
        Assert.That(homeTabViewModel.IsInstallLogVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task Install_LauncherOutsideInstallDir_DoesNotWarn()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        TestAppBuilder.EnvironmentProvider.ProcessPath = @"C:\Apps\Redux\redux-launcher.exe";

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        // Act
        await homeTabViewModel.InstallReduxCommand.ExecuteAsync(null);

        // Assert
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Can't Install Here", It.IsAny<string>(),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Never);
    }
}
