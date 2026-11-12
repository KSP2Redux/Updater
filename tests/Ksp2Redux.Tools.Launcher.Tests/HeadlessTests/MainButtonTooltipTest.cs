using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class MainButtonTooltipTest
{
    [AvaloniaTest]
    public async Task MainButton_NoInstallDetected_ShowsAnExplanatoryTooltipBoundOnTheActualButton()
    {
        // Arrange - no install registered at all, so the main button stays on its Launch-shaped
        // disabled default. This is the state a reviewer flagged as having lost its "why is this
        // disabled" tooltip when it was previously (over-)removed for repeating the button's label.
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();
        var installButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstallButton");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(homeTabViewModel.MainButtonEnabled, Is.False);
            Assert.That(homeTabViewModel.MainButtonTooltip, Does.Contain("not detected"));
            Assert.That(ToolTip.GetTip(installButton), Is.EqualTo(homeTabViewModel.MainButtonTooltip),
                "The button's actual ToolTip.Tip must be bound to MainButtonTooltip, not just set on the view model.");
            Assert.That(ToolTip.GetShowOnDisabled(installButton), Is.True,
                "A tooltip that only matters while disabled needs ShowOnDisabled, or it will never be seen.");
        });
    }

    [AvaloniaTest]
    public async Task MainButton_ReadyToLaunch_HasNoTooltip()
    {
        // Arrange - a normal, enabled state shouldn't show a tooltip that just repeats the button's
        // own label (the original, narrower complaint that led to the tooltip being removed entirely).
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        // No newer release than what's already installed, so the version dropdown defaults to the
        // synthetic "installed" entry and the button lands on its enabled Launch state.
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.IsAny<FeedInfo>()))
            .ReturnsAsync((FeedInfo f) => new ReleaseManifest
            {
                Channel = f.Filename.Split('-', '.')[1],
                GeneratedAt = new DateTime(2020, 1, 4),
                Patches = [],
                SchemaVersion = 1
            });

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        Assert.Multiple(() =>
        {
            Assert.That(homeTabViewModel.MainButtonEnabled, Is.True);
            Assert.That(homeTabViewModel.MainButtonTooltip, Is.Null.Or.Empty);
        });
    }
}
