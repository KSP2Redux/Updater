using System.Globalization;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.News;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.News;

public class NewsProviderServiceTest
{
    private static Feed FeedWith(string title) => new() { Items = [new FeedItem { Title = title }] };

    private static (NewsProviderService Service, List<string> Requested) Make(string culture,
        Func<string, Feed> respond)
    {
        List<string> requested = [];
        var localization = Mock.Of<ILocalizationService>(l => l.Culture == CultureInfo.GetCultureInfo(culture));
        var service = new NewsProviderService(localization, Mock.Of<ILogService>(), (url, _) =>
        {
            requested.Add(url);
            return Task.FromResult(respond(url));
        });
        return (service, requested);
    }

    [TestCase("en", "https://ksp2redux.org/blog/rss.xml")]
    [TestCase("en-US", "https://ksp2redux.org/blog/rss.xml")]
    [TestCase("de", "https://ksp2redux.org/de/blog/rss.xml")]
    [TestCase("pt-BR", "https://ksp2redux.org/pt-br/blog/rss.xml")]
    [TestCase("zh-Hans", "https://ksp2redux.org/zh-hans/blog/rss.xml")]
    [TestCase("zh-Hant", "https://ksp2redux.org/zh-hant/blog/rss.xml")]
    public void FeedUrl_FollowsTheSiteLanguageKeys(string culture, string expected)
    {
        Assert.That(NewsProviderService.FeedUrl(CultureInfo.GetCultureInfo(culture)), Is.EqualTo(expected));
    }

    [Test]
    public async Task English_ReadsOnlyTheEnglishFeed()
    {
        var (service, requested) = Make("en", _ => FeedWith("English"));

        var feed = await service.GetSyndicationFeed();

        Assert.That(feed.Items.Single().Title, Is.EqualTo("English"));
        Assert.That(requested, Is.EqualTo(new[] { NewsProviderService.ENGLISH_FEED }));
    }

    [Test]
    public async Task Language_UsesItsOwnFeedWhenItHasPosts()
    {
        var (service, requested) = Make("de", url => FeedWith(url.Contains("/de/") ? "Deutsch" : "English"));

        var feed = await service.GetSyndicationFeed();

        Assert.That(feed.Items.Single().Title, Is.EqualTo("Deutsch"));
        Assert.That(requested, Is.EqualTo(new[] { "https://ksp2redux.org/de/blog/rss.xml" }));
    }

    [Test]
    public async Task Language_FallsBackToEnglishWhenItsFeedIsEmpty()
    {
        var (service, requested) = Make("de", url => url.Contains("/de/") ? new Feed { Items = [] } : FeedWith("English"));

        var feed = await service.GetSyndicationFeed();

        Assert.That(feed.Items.Single().Title, Is.EqualTo("English"));
        Assert.That(requested, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Language_FallsBackToEnglishWhenItsFeedIsMissing()
    {
        var (service, requested) = Make("ru", url => url.Contains("/ru/") ? throw new HttpRequestException("404") : FeedWith("English"));

        var feed = await service.GetSyndicationFeed();

        Assert.That(feed.Items.Single().Title, Is.EqualTo("English"));
        Assert.That(requested.Last(), Is.EqualTo(NewsProviderService.ENGLISH_FEED));
    }

    [Test]
    public async Task EnglishFeedFailure_StillSurfaces()
    {
        var (service, _) = Make("en", _ => throw new HttpRequestException("offline"));

        await Assert.ThatAsync(() => service.GetSyndicationFeed(), Throws.TypeOf<HttpRequestException>());
    }
}
