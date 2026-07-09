using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test.HeadlessTests;

public class HomeHeroTest
{
    private static MainWindow ShowMainWindow()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestHelpers.MockMessageBoxAcceptAll();

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaTest]
    public void FeaturedUpdatePanel_ShowsTheNewestNewsItem()
    {
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items =
            [
                new FeedItem
                {
                    Title = "Beta 6 - Hotfix 4",
                    Content = "<p>SM+ heat shield, <b>procedural body flaps</b> and a batch of QoL fixes.</p>",
                    Link = "https://ksp2redux.org/blog/beta-6-hotfix-4",
                    PublishingDate = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc),
                },
                new FeedItem
                {
                    Title = "Older post",
                    Content = "<p>Old news.</p>",
                    Link = "https://ksp2redux.org/blog/older",
                    PublishingDate = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
        });

        var window = ShowMainWindow();
        var homeTab = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        Assert.Multiple(() =>
        {
            Assert.That(homeTab.HasFeaturedUpdate, Is.True);
            Assert.That(homeTab.FeaturedTitle, Is.EqualTo("Beta 6 - Hotfix 4"), "The NEWEST item must be featured.");
            Assert.That(homeTab.FeaturedExcerpt, Does.Contain("procedural body flaps"));
            Assert.That(homeTab.FeaturedExcerpt, Does.Not.Contain("<"), "The excerpt must be plain text, not HTML.");
            Assert.That(window.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "FeaturedPanel"), Is.True);
        });
    }

    [AvaloniaTest]
    public void EmptyNewsFeed_HidesTheFeaturedPanelInsteadOfShowingAnEmptyShell()
    {
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });

        ShowMainWindow();
        var homeTab = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        Assert.That(homeTab.HasFeaturedUpdate, Is.False);
    }

    [AvaloniaTest]
    public void StatusChip_ReflectsTheInstalledGameVersion()
    {
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();

        ShowMainWindow();
        var homeTab = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        Assert.Multiple(() =>
        {
            Assert.That(homeTab.StatusChipOk, Is.True);
            Assert.That(homeTab.StatusChipText, Does.StartWith("Ready - v"));
        });
    }

    [AvaloniaTest]
    public void NoInstallDetected_StatusChipSaysSo()
    {
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });

        ShowMainWindow();
        var homeTab = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        Assert.Multiple(() =>
        {
            Assert.That(homeTab.StatusChipOk, Is.False);
            Assert.That(homeTab.StatusChipText, Is.EqualTo("Game not detected"));
        });
    }
}

public class ExtractPlainTextExcerptTest
{
    [Test]
    public void StripsTagsDecodesEntitiesAndCollapsesWhitespace()
    {
        var html = "<p>We&apos;ve   just <b>released</b>\n a minor update.</p>";

        Assert.That(HomeTabViewModel.ExtractPlainTextExcerpt(html), Is.EqualTo("We've just released a minor update."));
    }

    [Test]
    public void LongContent_IsTruncatedAtAWordBoundaryWithEllipsis()
    {
        var html = string.Join(" ", Enumerable.Repeat("word", 200));

        var excerpt = HomeTabViewModel.ExtractPlainTextExcerpt(html, maxLength: 50);

        Assert.Multiple(() =>
        {
            Assert.That(excerpt, Does.EndWith("..."));
            Assert.That(excerpt.Length, Is.LessThanOrEqualTo(53));
            Assert.That(excerpt, Does.Not.Contain("wo..."), "Truncation must land on a word boundary.");
        });
    }

    [Test]
    public void EmptyContent_YieldsEmptyString()
    {
        Assert.That(HomeTabViewModel.ExtractPlainTextExcerpt(""), Is.Empty);
    }
}
