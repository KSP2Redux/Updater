using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class BackgroundMotionSettingTest
{
    private static (MainWindow Window, MainWindowViewModel ViewModel, ParallaxBackground Background) Start()
    {
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();

        var viewModel = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
        window.Show();
        Pump();
        return (window, viewModel, window.FindControl<ParallaxBackground>("BackgroundLayers")!);
    }

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void MotionOn_MovingTheMouse_ShiftsTheBackground()
    {
        var (window, viewModel, background) = Start();
        Assert.That(viewModel.SettingsTab.ParallaxMotion, Is.True, "Motion should be on by default.");

        window.MouseMove(new Point(20, 20), RawInputModifiers.None);
        Pump();

        Assert.That(background.Offset.Length, Is.GreaterThan(0.1));
    }

    [AvaloniaTest]
    public void MotionOff_MovingTheMouse_LeavesTheBackgroundCentred()
    {
        var (window, viewModel, background) = Start();
        viewModel.SettingsTab.ParallaxMotion = false;

        window.MouseMove(new Point(20, 20), RawInputModifiers.None);
        Pump();

        Assert.That(background.Offset, Is.EqualTo(default(Vector)));
    }

    [AvaloniaTest]
    public void TurningMotionOff_EasesAnExistingOffsetBackToCentre()
    {
        var (window, viewModel, background) = Start();
        window.MouseMove(new Point(20, 20), RawInputModifiers.None);
        Pump();

        viewModel.SettingsTab.ParallaxMotion = false;
        for (var i = 0; i < 10; i++) Pump();

        Assert.That(background.Offset.Length, Is.LessThan(0.01));
    }

    [AvaloniaTest]
    public void TurningMotionOff_IsSavedToTheLauncherConfig()
    {
        var (_, viewModel, _) = Start();

        viewModel.SettingsTab.ParallaxMotion = false;

        var config = TestAppBuilder.ServiceProvider.GetRequiredService<ILauncherConfigService>().Config;
        Assert.That(config.ParallaxMotion, Is.False);
        Assert.That(TestAppBuilder.FileSystem.File.ReadAllText(config.StoragePath), Does.Contain("\"ParallaxMotion\": false"));
    }
}
