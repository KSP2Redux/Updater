using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.News;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.News;

public class NewsProviderServiceTest
{
    private static (NewsProviderService Service, List<string> Requested) Make(Func<string, Feed> respond)
    {
        List<string> requested = [];
        var service = new NewsProviderService((url, _) =>
        {
            requested.Add(url);
            return Task.FromResult(respond(url));
        });
        return (service, requested);
    }

    // The site has one feed for every language: /ja/blog/rss.xml and the like answer with an HTML 404 page.
    [Test]
    public async Task GetSyndicationFeed_ReadsOnlyTheSiteFeed()
    {
        var (service, requested) = Make(_ => new Feed { Items = [new FeedItem { Title = "News" }] });

        var feed = await service.GetSyndicationFeed();

        Assert.That(feed.Items.Single().Title, Is.EqualTo("News"));
        Assert.That(requested, Is.EqualTo(new[] { "https://ksp2redux.org/blog/rss.xml" }));
    }

    [Test]
    public async Task FeedFailure_StillSurfaces()
    {
        var (service, _) = Make(_ => throw new HttpRequestException("offline"));

        await Assert.ThatAsync(() => service.GetSyndicationFeed(), Throws.TypeOf<HttpRequestException>());
    }
}
