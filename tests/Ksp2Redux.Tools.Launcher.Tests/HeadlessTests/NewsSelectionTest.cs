using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class NewsSelectionTest
{
    [AvaloniaTest]
    public void OpeningAPost_MarksOnlyThatRowInTheSidebar()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items =
            [
                new FeedItem { Link = "https://example.com/a", Title = "A", Content = "<p>a</p>", PublishingDate = new DateTime(2026, 11, 1) },
                new FeedItem { Link = "https://example.com/b", Title = "B", Content = "<p>b</p>", PublishingDate = new DateTime(2026, 11, 2) },
            ]
        });
        TestHelpers.MockMessageBoxAcceptAll();
        var viewModel = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        new MainWindow { DataContext = viewModel }.Show();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        viewModel.CommunityTab.SetSelectedNewsId("https://example.com/b");

        var rows = viewModel.NewsCollectionViewModel.NewsCollection;
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Where(row => row.IsSelected).Select(row => row.NewsId), Is.EqualTo(new[] { "https://example.com/b" }));
    }
}
