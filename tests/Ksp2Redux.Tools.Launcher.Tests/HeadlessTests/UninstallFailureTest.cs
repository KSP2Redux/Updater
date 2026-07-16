using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class UninstallFailureTest
{
    [AvaloniaTest]
    public async Task UninstallRedux_RestoreCacheThrows_ShowsErrorInsteadOfCrashing()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        const string installDir = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2";
        TestAppBuilder.FileSystem.File.WriteAllBytes(TestAppBuilder.FileSystem.Path.Combine(installDir, "uninstall.zip"), [0x00]);
        TestAppBuilder.ZipFileService
            .Setup(z => z.OpenRead(TestAppBuilder.FileSystem.Path.Combine(installDir, "uninstall.zip")))
            .Throws(new IOException("uninstall.zip is locked by another process"));

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var settingsTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        // Act
        Exception? thrown = null;
        try
        {
            await settingsTabViewModel.UninstallRedux();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        Dispatcher.UIThread.RunJobs();

        // Assert
        Assert.That(thrown, Is.Null, "Uninstall failure must not propagate as an unhandled exception.");
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Error!",
                It.Is<string>(s => s.Contains("Couldn't uninstall Redux") && s.Contains("locked by another process")),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }
}
