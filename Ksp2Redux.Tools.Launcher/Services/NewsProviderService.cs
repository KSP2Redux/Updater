using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml;
using CodeHollow.FeedReader;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface INewsProviderService
{
    Task<Feed> GetSyndicationFeed();
}

public class NewsProviderService : INewsProviderService
{
    // private readonly string _rawRepoTargetUrl = "https://raw.githubusercontent.com/SamBret/LauncherNews/refs/heads/main/";
    // private readonly string _tomlTargetFile = "news.toml";
    // private string _tomlTargetUrl => _rawRepoTargetUrl + _tomlTargetFile;
    // private string _imageTargetUrl => _rawRepoTargetUrl + "images/";

    private const string RssFeed = "https://ksp2redux.org/blog/rss.xml";
    public async Task<Feed> GetSyndicationFeed()
    {
        var items = await FeedReader.ReadAsync(RssFeed);
        return items ?? new Feed();
    }
}