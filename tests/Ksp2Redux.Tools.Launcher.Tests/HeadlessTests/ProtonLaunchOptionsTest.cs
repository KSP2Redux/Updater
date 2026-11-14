using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Ksp2Redux.Tools.Launcher.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class ProtonLaunchOptionsTest
{
    private const string EXE = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe";
    private MainWindow _window = null!;

    private (HomeTabViewModel Home, SettingsTabViewModel Settings, IKsp2InstallService Installs) Start(
        bool isLinux = true, bool isMacOS = false)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(isLinux);
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(isMacOS);
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestAppBuilder.ProtonRuntimeService
            .Setup(p => p.PrepareAsync(It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        TestAppBuilder.ProtonRuntimeService
            .Setup(p => p.CreateLaunchInfo(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new ProcessStartInfo("python3"));
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForStartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var installs = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        installs.AddInstall(EXE);
        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };
        _window = window;
        window.Show();
        Pump();
        var home = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();
        home.SelectedVersion = home.Versions.Single(v => v.Channel == "installed");
        return (home,
            TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>(), installs);
    }

    private static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static TaskCompletionSource HoldPreparation()
    {
        var prepared = new TaskCompletionSource();
        TestAppBuilder.ProtonRuntimeService
            .Setup(p => p.PrepareAsync(It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Returns((Action<LogMessage> _, CancellationToken token) => prepared.Task.WaitAsync(token));
        return prepared;
    }

    private static CheckBox ShowProtonToggle(SettingsTabViewModel settings)
    {
        var view = new SettingsTabView { DataContext = settings };
        var window = new Window { Content = view };
        window.Show();
        Pump();
        return view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "LaunchThroughProtonToggle");
    }

    [AvaloniaTest]
    public async Task CancellingAnOrdinaryUninstallRestoresProfileSwitching()
    {
        var (home, _, installs) = Start(isLinux: false);
        var second = installs.AddInstall(EXE, "comparison profile");
        Pump();
        Assert.That(home.Installs.Count, Is.EqualTo(2));
        var fileSystem = TestAppBuilder.FileSystem;
        fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(fileSystem.Path.GetDirectoryName(EXE)!, "uninstall.zip"), new byte[] { 0 });
        var started = new TaskCompletionSource();
        TestAppBuilder.BundleConversion
            .Setup(service => service.Revert(It.IsAny<string>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Action<LogMessage>, CancellationToken>((_, _, cancellation) =>
            {
                started.TrySetResult();
                cancellation.WaitHandle.WaitOne();
                cancellation.ThrowIfCancellationRequested();
            });

        Task uninstalling = home.UninstallRedux();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        home.RefreshMainButtonState();
        Pump();
        Assert.That(home.IsInstallSwitcherEnabled, Is.False);
        home.CancelCurrentMainButtonActionCommand.Execute(null);
        await uninstalling;
        Pump();

        Assert.That(home.IsInstallSwitcherEnabled, Is.True,
            $"Profiles={home.Installs.Count}, entries={installs.Entries.Count}, button={home.MainButtonShown}");
        home.SelectedInstall = home.Installs.Single(choice => choice.Id == second.Id);
        Pump();
        Assert.That(installs.ActiveEntry!.Id, Is.EqualTo(second.Id));
    }

    [AvaloniaTest]
    public void ProtonOption_OnLinux_CanBeSelectedInSettings()
    {
        var (_, settings, installs) = Start();
        var toggle = ShowProtonToggle(settings);

        toggle.IsChecked = true;
        Pump();

        Assert.That(toggle.IsEffectivelyVisible, Is.True);
        Assert.That(installs.ActiveEntry!.LaunchThroughProton, Is.True);
    }

    [AvaloniaTest]
    public void ProtonOption_OnWindows_IsHidden()
    {
        var (_, settings, _) = Start(isLinux: false);

        Assert.That(ShowProtonToggle(settings).IsEffectivelyVisible, Is.False);
    }

    [AvaloniaTest]
    public void ProtonOption_OnMac_IsHidden()
    {
        var (_, settings, _) = Start(isLinux: false, isMacOS: true);

        Assert.That(ShowProtonToggle(settings).IsEffectivelyVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task ManagedLaunch_WaitsForPreparationAndTracksTheGameAfterwards()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        settings.SelectedInstall.LaunchArguments = "-screen-width 1280";
        var prepared = HoldPreparation();
        var exited = new TaskCompletionSource();
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForStartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => exited.Task.WaitAsync(token));

        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        home.RefreshMainButtonState();
        Pump();

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));
        Assert.That(home.MainButtonEnabled, Is.True);
        Assert.That(home.IsInstallLogVisible, Is.True);

        prepared.SetResult();
        Pump();

        TestAppBuilder.ProtonRuntimeService.Verify(p => p.CreateLaunchInfo(
            EXE, It.IsAny<string>(), "-screen-width 1280"), Times.Once);
        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.Is<ProcessStartInfo>(s => s.FileName == "python3")), Times.Once);
        TestAppBuilder.GameProcessService.Verify(g => g.WaitForExitAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Cancel));

        exited.SetResult();
        await launching;
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Launch));
    }

    [AvaloniaTest]
    public async Task CancellingPreparation_StartsNothingAndAllowsAnotherLaunch()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        HoldPreparation();
        var launching = home.LaunchGameCommand.ExecuteAsync(null);

        home.CancelCurrentMainButtonActionCommand.Execute(null);
        await launching;

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        Assert.That(home.InstallLogText, Does.Contain("cancelled"));
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Launch));
        Assert.That(home.MainButtonEnabled, Is.True);
        TestAppBuilder.ProtonRuntimeService
            .Setup(p => p.PrepareAsync(It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Once);
    }

    [AvaloniaTest]
    public async Task ClosingDuringPreparation_WaitsForCancelledProcessesBeforeTheWindowCloses()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        var cancelled = new TaskCompletionSource();
        var cleanupFinished = new TaskCompletionSource();
        TestAppBuilder.ProtonRuntimeService
            .Setup(p => p.PrepareAsync(It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Action<LogMessage> _, CancellationToken token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    await cleanupFinished.Task;
                    throw;
                }
            });
        var launching = home.LaunchGameCommand.ExecuteAsync(null);

        _window.Close();
        await cancelled.Task;
        Pump();

        Assert.That(_window.IsVisible, Is.True, "Cancellation must finish while the dispatcher is still running.");
        Assert.That(launching.IsCompleted, Is.False);
        cleanupFinished.SetResult();
        await launching;
        Pump();

        Assert.That(_window.IsVisible, Is.False);
        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [AvaloniaTest]
    public async Task ClosingAfterTheGameStarts_LeavesTheGameRunning()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        var exited = new TaskCompletionSource();
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForStartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        TestAppBuilder.GameProcessService
            .Setup(g => g.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => exited.Task.WaitAsync(token));
        var launching = home.LaunchGameCommand.ExecuteAsync(null);
        Pump();

        _window.Close();
        Pump();

        Assert.That(_window.IsVisible, Is.False);
        Assert.That(launching.IsCompleted, Is.False);
        TestAppBuilder.GameProcessService.Verify(g => g.StopAsync(), Times.Never);
        exited.SetResult();
        await launching;
    }

    [AvaloniaTest]
    public async Task FailedPreparation_ShowsTheReasonAndDoesNotStartTheGame()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        TestAppBuilder.ProtonRuntimeService
            .Setup(p => p.PrepareAsync(It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Checksum mismatch"));

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Couldn't Launch", It.Is<string>(s => s.Contains("Checksum mismatch") && s.Contains("retry")),
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Once);
        Assert.That(home.MainButtonShown, Is.EqualTo(HomeTabViewModel.MainButtonState.Launch));
        Assert.That(home.MainButtonEnabled, Is.True);
        Assert.That(home.InstallLogText, Does.Contain("Checksum mismatch"));
    }

    [AvaloniaTest]
    public async Task InstallRemovedDuringPreparation_IsNotLaunched()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        var prepared = HoldPreparation();
        var launching = home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.FileSystem.File.Delete(EXE);
        prepared.SetResult();
        await launching;

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        Assert.That(home.InstallLogText, Does.Contain("changed"));
    }

    [AvaloniaTest]
    public async Task LaunchMethodChangedDuringPreparation_IsNotLaunchedWithTheOldMethod()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughProton = true;
        var prepared = HoldPreparation();
        var launching = home.LaunchGameCommand.ExecuteAsync(null);

        settings.SelectedInstall.LaunchThroughSteam = true;
        prepared.SetResult();
        await launching;

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [AvaloniaTest]
    public async Task LinuxSteamLaunch_PreservesSteamAndNeverPreparesManagedProton()
    {
        var (home, settings, _) = Start();
        settings.SelectedInstall!.LaunchThroughSteam = true;

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.Is<ProcessStartInfo>(s =>
            s.FileName == "steam://rungameid/954850" && s.UseShellExecute)), Times.Once);
        TestAppBuilder.ProtonRuntimeService.Verify(p => p.PrepareAsync(
            It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [AvaloniaTest]
    public async Task LinuxWithoutALaunchMethod_DoesNotExecuteTheWindowsBinary()
    {
        var (home, _, _) = Start();

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Couldn't Launch", It.Is<string>(s => s.Contains("Launch via Proton") && s.Contains("Launch via Steam")),
            It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Once);
    }

    [AvaloniaTest]
    public async Task WindowsWithAProtonFlag_KeepsTheExistingDirectLaunch()
    {
        var (home, _, installs) = Start(isLinux: false);
        installs.ActiveEntry!.LaunchThroughProton = true;

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.Is<ProcessStartInfo>(s => s.FileName == EXE)), Times.Once);
        TestAppBuilder.ProtonRuntimeService.Verify(p => p.PrepareAsync(
            It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [AvaloniaTest]
    public async Task MacWithAProtonFlag_KeepsTheExistingWineLaunch()
    {
        var (home, _, installs) = Start(isLinux: false, isMacOS: true);
        installs.ActiveEntry!.LaunchThroughProton = true;

        await home.LaunchGameCommand.ExecuteAsync(null);

        TestAppBuilder.WineRuntimeService.Verify(w => w.Detect(), Times.AtLeastOnce);
        TestAppBuilder.ProtonRuntimeService.Verify(p => p.PrepareAsync(
            It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
        TestAppBuilder.GameProcessService.Verify(g => g.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [AvaloniaTest]
    public void ChangingLaunchMethods_PersistsOnlyTheChosenMethod()
    {
        var (_, settings, _) = Start();
        var row = settings.SelectedInstall!;
        row.LaunchThroughSteam = true;
        row.LaunchThroughProton = true;

        var config = TestAppBuilder.ServiceProvider.GetRequiredService<ILauncherConfigService>().Config;
        var stored = JsonSerializer.Deserialize<LauncherConfig>(TestAppBuilder.FileSystem.File.ReadAllText(config.StoragePath))!;
        var entry = stored.Ksp2Installs.Single(e => e.Id == row.Id);
        Assert.That(entry.LaunchThroughProton, Is.True);
        Assert.That(entry.LaunchThroughSteam, Is.False);
        Assert.That(row.LaunchThroughSteam, Is.False);

        row.LaunchThroughSteam = true;
        stored = JsonSerializer.Deserialize<LauncherConfig>(TestAppBuilder.FileSystem.File.ReadAllText(config.StoragePath))!;
        entry = stored.Ksp2Installs.Single(e => e.Id == row.Id);
        Assert.That(entry.LaunchThroughSteam, Is.True);
        Assert.That(entry.LaunchThroughProton, Is.False);
        Assert.That(row.LaunchThroughProton, Is.False);
    }
}
