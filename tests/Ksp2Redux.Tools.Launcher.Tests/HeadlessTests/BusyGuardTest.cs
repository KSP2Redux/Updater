using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class BusyGuardTest
{
    private const string BUSY = "Close KSP2 and wait for the launcher to finish what it is doing, then try again.";

    private static MainWindowViewModel Start()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        return TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void VerifyBusyMessageShown(Times times) =>
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(It.IsAny<string>(), BUSY,
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), times);

    [AvaloniaTest]
    public async Task SettingsActions_WhileAnOperationRuns_AreRefusedBeforeAsking()
    {
        var viewModel = Start();
        var finish = new TaskCompletionSource();
        var running = viewModel.HomeTab.RunBlockingOperationAsync(() => finish.Task);

        var entry = new Ksp2InstallEntry { Id = Guid.NewGuid(), Name = "Test", ExePath = @"C:\Games\KSP2\KSP2_x64.exe" };
        viewModel.SettingsTab.SelectedInstall = new Ksp2InstallRowViewModel(TestAppBuilder.FileSystem,
            TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>(), TestAppBuilder.MessageBoxService.Object,
            LocalizationService.Default, entry, isActive: true);

        await viewModel.SettingsTab.UninstallRedux();
        await viewModel.SettingsTab.InstallFromPatchFile();
        await viewModel.SettingsTab.UninstallKsp2();

        VerifyBusyMessageShown(Times.Exactly(3));
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(It.IsAny<string>(), It.IsAny<string>(),
            ButtonEnum.YesNo, It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Never,
            "Nothing should ask to confirm an action that would run on top of another.");
        finish.SetResult();
        await running;
    }

    [AvaloniaTest]
    public async Task BlockingOperation_TurnsTheMainButtonOffUntilItEnds()
    {
        var home = Start().HomeTab;
        var finish = new TaskCompletionSource();

        var running = home.RunBlockingOperationAsync(() => finish.Task);

        Assert.That(home.IsOperationRunning, Is.True);
        Assert.That(home.MainButtonEnabled, Is.False);
        Assert.That(() => home.RunBlockingOperationAsync(() => Task.CompletedTask), Throws.InvalidOperationException);
        finish.SetResult();
        await running;
        Assert.That(home.IsOperationRunning, Is.False);
    }

    [AvaloniaTest]
    public async Task ClosingTheWindow_WhileAnOperationRuns_KeepsItOpen()
    {
        var viewModel = Start();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Pump();
        var finish = new TaskCompletionSource();
        var running = viewModel.HomeTab.RunBlockingOperationAsync(() => finish.Task);

        window.Close();
        Pump();

        Assert.That(window.IsVisible, Is.True);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync("Still working", It.IsAny<string>(),
            ButtonEnum.Ok, Icon.Warning, It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Once);

        finish.SetResult();
        await running;
        window.Close();
        Pump();
        Assert.That(window.IsVisible, Is.False);
    }
}
