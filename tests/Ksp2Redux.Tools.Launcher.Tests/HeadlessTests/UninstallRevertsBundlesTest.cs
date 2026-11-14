using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Wrappers;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

/// <summary>
/// The Uninstall Redux button deletes the Redux folder, and with it the journal that records the converted stock
/// bundles. Those bundles live in StreamingAssets, which the restore leaves alone, so a button that skipped the
/// revert left them converted and the main menu magenta.
/// </summary>
public class UninstallRevertsBundlesTest
{
    private const string InstallDir = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2";

    [AvaloniaTest]
    public async Task UninstallRedux_UndoesTheBundleConversionBeforeRestoringTheStockFiles()
    {
        // Arrange
        var settings = Start();
        List<string> order = [];
        TestAppBuilder.BundleConversion
            .Setup(b => b.RevertAsync(It.IsAny<string>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("revert bundles"));
        TestAppBuilder.ZipFileService
            .Setup(z => z.OpenRead(It.IsAny<string>()))
            .Callback(() => order.Add("restore files"))
            .Returns(Mock.Of<IZipArchive>());

        // Act
        await settings.UninstallRedux();
        Dispatcher.UIThread.RunJobs();

        // Assert: the restore deletes the journal, so the revert has to come first.
        Assert.That(order, Is.EqualTo(new[] { "revert bundles", "restore files" }));
    }

    [AvaloniaTest]
    public async Task UninstallRedux_BundlesCannotBePutBack_KeepsReduxInstalledAndSaysWhy()
    {
        // Arrange
        var settings = Start();
        TestAppBuilder.BundleConversion
            .Setup(b => b.RevertAsync(It.IsAny<string>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Throws(new IOException("3 stock bundles hold converted shaders that only Steam's copies can put back."));

        // Act
        await settings.UninstallRedux();
        Dispatcher.UIThread.RunJobs();

        // Assert: restoring the stock files anyway would delete the journal and strand the converted bundles.
        TestAppBuilder.ZipFileService.Verify(z => z.OpenRead(It.IsAny<string>()), Times.Never);
        Assert.That(TestAppBuilder.FileSystem.File.Exists(TestAppBuilder.FileSystem.Path.Combine(InstallDir, "uninstall.zip")), Is.True);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Error!",
                It.Is<string>(s => s.Contains("Couldn't uninstall Redux") && s.Contains("only Steam's copies can put back")),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }

    private static SettingsTabViewModel Start()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.FileSystem.File.WriteAllBytes(TestAppBuilder.FileSystem.Path.Combine(InstallDir, "uninstall.zip"), [0x00]);

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
    }
}
