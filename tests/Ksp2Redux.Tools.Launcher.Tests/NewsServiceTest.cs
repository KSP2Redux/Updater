using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests;

public class NewsServiceTest
{
    private static (NewsService Service, Mock<INewsProviderService> Provider, Mock<ILogService> Log) MakeService()
    {
        var provider = new Mock<INewsProviderService>();
        var log = new Mock<ILogService>();
        return (new NewsService(provider.Object, log.Object), provider, log);
    }

    [Test]
    public async Task FetchNews_OneItemMissingPublishDate_SkipsItButKeepsTheRest()
    {
        var (service, provider, log) = MakeService();
        provider.Setup(p => p.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items =
            [
                new FeedItem { Title = "Good post", Link = "https://a", PublishingDate = new DateTime(2026, 1, 1) },
                new FeedItem { Title = "Missing date", Link = "https://b", PublishingDate = null },
                new FeedItem { Title = "Another good post", Link = "https://c", PublishingDate = new DateTime(2026, 1, 2) }
            ]
        });

        await service.FetchNews();
        var news = await service.FindAllNews();

        Assert.That(news.Select(n => n.Title), Is.EquivalentTo(["Good post", "Another good post"]));
        log.Verify(l => l.Warn(It.Is<string>(s => s.Contains("Missing date")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public async Task GetNews_ByStableId_ReturnsTheSameItemAcrossARefetchThatChangesListLength()
    {
        var (service, provider, _) = MakeService();
        provider.Setup(p => p.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items =
            [
                new FeedItem { Title = "First", Link = "https://a", PublishingDate = new DateTime(2026, 1, 1) },
                new FeedItem { Title = "Second", Link = "https://b", PublishingDate = new DateTime(2026, 1, 2) }
            ]
        });
        await service.FetchNews();
        var secondId = (await service.FindAllNews()).Single(n => n.Title == "Second").Id;

        // Simulate the feed being refetched with a different shape - the old index into the list
        // would now point somewhere else (or be out of range), but the stable id still resolves.
        provider.Setup(p => p.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items = [new FeedItem { Title = "Second", Link = "https://b", PublishingDate = new DateTime(2026, 1, 2) }]
        });
        await service.FetchNews();

        Assert.That(service.GetNews(secondId)?.Title, Is.EqualTo("Second"));
    }

    [Test]
    public async Task GetNews_IdNoLongerPresent_ReturnsNullInsteadOfThrowing()
    {
        var (service, provider, _) = MakeService();
        provider.Setup(p => p.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        await service.FetchNews();

        Assert.That(service.GetNews("stale-id"), Is.Null);
    }
}
