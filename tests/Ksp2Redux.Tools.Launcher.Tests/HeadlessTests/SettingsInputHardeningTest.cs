using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class SettingsInputHardeningTest
{
    private static async Task<SettingsTabViewModel> BootstrapAsync()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
    }

    [AvaloniaTest]
    public async Task RemoveSelectedInstall_Declined_KeepsTheInstall()
    {
        // Arrange
        var settingsTabViewModel = await BootstrapAsync();
        var ksp2InstallService = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        ksp2InstallService.AddInstall(@"C:\Other\KSP2_x64.exe", "Second install");

        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                "Confirm", It.IsAny<string>(), It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()))
            .ReturnsAsync(ButtonResult.No);

        var countBefore = ksp2InstallService.Entries.Count;

        // Act
        await settingsTabViewModel.RemoveSelectedInstallCommand.ExecuteAsync(null);

        // Assert
        Assert.That(ksp2InstallService.Entries, Has.Count.EqualTo(countBefore));
    }

    [AvaloniaTest]
    public async Task RemoveSelectedInstall_Confirmed_RemovesTheInstall()
    {
        // Arrange
        var settingsTabViewModel = await BootstrapAsync();
        var ksp2InstallService = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        ksp2InstallService.AddInstall(@"C:\Other\KSP2_x64.exe", "Second install");

        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                "Confirm", It.IsAny<string>(), It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()))
            .ReturnsAsync(ButtonResult.Yes);

        var countBefore = ksp2InstallService.Entries.Count;

        // Act
        await settingsTabViewModel.RemoveSelectedInstallCommand.ExecuteAsync(null);

        // Assert
        Assert.That(ksp2InstallService.Entries, Has.Count.EqualTo(countBefore - 1));
    }

    [AvaloniaTest]
    public async Task AddInstall_AlreadyInProgress_ReturnsWithoutTouchingTheFilePicker()
    {
        // Arrange - simulate a fast double-click by marking the flow as already in progress.
        var settingsTabViewModel = await BootstrapAsync();
        settingsTabViewModel.IsAddingInstall = true;

        // Act
        await settingsTabViewModel.AddInstallCommand.ExecuteAsync(null);

        // Assert - the re-entrancy guard returned early, so the file-picker failure path
        // (which would show this dialog, since there's no real window in this test) never ran.
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Error!", It.Is<string>(s => s.Contains("file picker")),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Never);
    }
}
