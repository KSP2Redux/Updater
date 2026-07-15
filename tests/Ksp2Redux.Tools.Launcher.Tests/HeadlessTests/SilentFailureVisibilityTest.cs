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

public class SilentFailureVisibilityTest
{
    [AvaloniaTest]
    public async Task UpdateLauncher_ThrowingUnexpectedly_ShowsFailureDialogInsteadOfFailingSilently()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync())
            .ThrowsAsync(new InvalidOperationException("boom"));

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        // Act
        await homeTabViewModel.UpdateLauncher();
        Dispatcher.UIThread.RunJobs();

        // Assert - the failure surfaces as a dialog, not just a log line
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Update Failed!",
            It.Is<string>(s => s.Contains("boom")),
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }
}
