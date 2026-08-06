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

public class FeedRefreshFailureTest
{
    private const string DefaultChannel = "beta";
    private const string OtherChannel = "stable";

    [AvaloniaTest]
    public async Task RefreshFeeds_ManifestFetchThrows_ShowsWarningBannerUntilNextSuccessfulRefresh()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        ReleaseManifest MakeManifest(string channel) => new()
        {
            Channel = channel,
            GeneratedAt = new DateTime(2020, 1, 4),
            Patches = [],
            SchemaVersion = 1
        };

        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel))))
            .ReturnsAsync(MakeManifest(DefaultChannel));
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel) == false)))
            .ReturnsAsync(MakeManifest(OtherChannel));

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();
        Assert.That(homeTabViewModel.FeedRefreshFailed, Is.False);

        var warningText = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
            .FirstOrDefault(t => t.Name == "FeedRefreshWarning");
        Assert.That(warningText, Is.Not.Null);
        Assert.That(warningText!.IsVisible, Is.False);

        // Act - simulate the feed fetch failing on the next refresh (e.g. network drop)
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.IsAny<FeedInfo>()))
            .ThrowsAsync(new HttpRequestException("network unreachable"));

        await homeTabViewModel.RefreshFeedsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Assert - banner shows while the feed is failing
        Assert.That(homeTabViewModel.FeedRefreshFailed, Is.True);
        Assert.That(warningText.IsVisible, Is.True);

        // Act - the feed recovers on the following refresh
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel))))
            .ReturnsAsync(MakeManifest(DefaultChannel));
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel) == false)))
            .ReturnsAsync(MakeManifest(OtherChannel));

        await homeTabViewModel.RefreshFeedsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Assert - banner clears once refreshes succeed again
        Assert.That(homeTabViewModel.FeedRefreshFailed, Is.False);
        Assert.That(warningText.IsVisible, Is.False);
    }
}
