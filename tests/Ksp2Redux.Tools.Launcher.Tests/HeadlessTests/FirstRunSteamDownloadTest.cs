using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

// Starting with no installs asked "Download it now?" before the main window existed, so the prompt had no
// owner and opened behind the launcher. Startup waited on it before loading the release feeds, which left
// the Install dropdown and the release channels empty until a restart.
public class FirstRunSteamDownloadTest
{
    private const string EXE = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe";
    private const string NOT_FOUND_TITLE = "KSP2 Install Not Found";

    private static void ArrangeNoInstalls(Task<ButtonResult> notFoundAnswer, Task<string?> download)
    {
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();
        // Keep the game files but hide them from Steam detection, as on a Mac.
        TestAppBuilder.FileSystem.File.Delete(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_954850.acf");
        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                NOT_FOUND_TITLE, It.IsAny<string>(), It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()))
            .Returns(notFoundAnswer);
        TestAppBuilder.SteamDialogService.Setup(d => d.DownloadGameAsync()).Returns(download);
    }

    private static MainWindow ShowMainWindow()
    {
        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void RunJobs()
    {
        for (var i = 0; i < 20; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void VerifyNotFoundPromptShown(Times times) =>
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            NOT_FOUND_TITLE, It.IsAny<string>(), It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), times);

    [AvaloniaTest]
    public void Startup_NoInstalls_WaitsForTheMainWindowBeforePrompting()
    {
        // Arrange
        ArrangeNoInstalls(Task.FromResult(ButtonResult.No), Task.FromResult<string?>(null));

        // Act
        TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        RunJobs();

        // Assert
        VerifyNotFoundPromptShown(Times.Never());
        ShowMainWindow();
        VerifyNotFoundPromptShown(Times.Once());
    }

    [AvaloniaTest]
    public void Startup_PromptLeftUnanswered_ReleaseChannelsAreLoaded()
    {
        // Arrange
        ArrangeNoInstalls(new TaskCompletionSource<ButtonResult>().Task, Task.FromResult<string?>(null));

        // Act
        ShowMainWindow();

        // Assert
        VerifyNotFoundPromptShown(Times.Once());
        Assert.That(TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>().ValidChannels, Is.Not.Empty);
    }

    // What the player actually did: ignored the hidden prompt and downloaded from the Settings tab instead.
    [AvaloniaTest]
    public void Startup_PromptLeftUnanswered_InstallAddedElsewhereIsSelectedEverywhere()
    {
        // Arrange
        ArrangeNoInstalls(new TaskCompletionSource<ButtonResult>().Task, Task.FromResult<string?>(null));
        var window = ShowMainWindow();
        var main = (MainWindowViewModel)window.DataContext!;
        main.CurrentTab = MainWindowViewModel.SettingsTabId;
        RunJobs();

        // Act
        var installId = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>().AddInstall(EXE, "KSP2 (Steam download)").Id;
        RunJobs();

        // Assert
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        Assert.That(settings.SelectedInstall?.Id, Is.EqualTo(installId));
        Assert.That(TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>().SelectedInstall?.Id, Is.EqualTo(installId));
        var dropdown = window.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, settings.Installs));
        Assert.That((dropdown.SelectedItem as Ksp2InstallRowViewModel)?.Id, Is.EqualTo(installId));
    }

    // Loading the release channels must not wait for a download that takes many minutes.
    [AvaloniaTest]
    public void FirstRunDownload_WhileDownloading_ReleaseChannelsAreAlreadyLoaded()
    {
        // Arrange
        ArrangeNoInstalls(Task.FromResult(ButtonResult.Yes), new TaskCompletionSource<string?>().Task);

        // Act
        ShowMainWindow();

        // Assert
        TestAppBuilder.SteamDialogService.Verify(d => d.DownloadGameAsync(), Times.Once);
        Assert.That(TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>().ValidChannels, Is.Not.Empty);
    }

    [AvaloniaTest]
    public void FirstRunDownload_Finished_NewInstallIsSelectedEverywhere()
    {
        // Arrange
        var download = new TaskCompletionSource<string?>();
        ArrangeNoInstalls(Task.FromResult(ButtonResult.Yes), download.Task);
        ShowMainWindow();

        // Act
        var installId = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>().AddInstall(EXE, "KSP2 (Steam download)").Id;
        download.SetResult(EXE);
        RunJobs();

        // Assert
        Assert.That(TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>().SelectedInstall?.Id, Is.EqualTo(installId));
        Assert.That(TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>().SelectedInstall?.Id, Is.EqualTo(installId));
    }
}
