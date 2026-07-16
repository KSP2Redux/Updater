using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class DiagnosticInfoTest
{
    [AvaloniaTest]
    public async Task BuildDiagnosticInfo_ActiveInstallAndLogPresent_BundlesEverythingIntoOneBlock()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var settingsTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        // Act
        string diagnosticInfo = settingsTabViewModel.BuildDiagnosticInfo();

        // Assert
        Assert.That(diagnosticInfo, Does.Contain("KSP2 Redux Launcher v"));
        Assert.That(diagnosticInfo, Does.Contain("Active install:"));
        Assert.That(diagnosticInfo, Does.Contain("--- Recent log ---"));
    }
}
