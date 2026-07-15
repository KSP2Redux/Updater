using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class VersionDetectionFailureTest
{
    [AvaloniaTest]
    public async Task Startup_GameVersionDetectionThrows_ShowsPlainLanguageDialogInsteadOfRawException()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        // Override the version-reading mock (set up by MockKsp2StockSteamInstall) to blow up instead.
        TestAppBuilder.ModuleDefinitionService
            .Setup(m => m.ReadModule(It.IsAny<string>()))
            .Throws(new InvalidOperationException("corrupt PE header"));

        // Act
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert - the user gets a plain-language explanation, not a dumped exception type/stack trace.
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Couldn't Detect Game Version",
                It.Is<string>(s => s.Contains("couldn't figure out") && !s.Contains("InvalidOperationException") && !s.Contains("corrupt PE header")),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }
}
