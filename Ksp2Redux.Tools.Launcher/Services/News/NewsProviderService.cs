using System;
using System.Threading;
using System.Threading.Tasks;
using CodeHollow.FeedReader;

namespace Ksp2Redux.Tools.Launcher.Services.News;

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

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const string RssFeed = "https://ksp2redux.org/blog/rss.xml";

    public async Task<Feed> GetSyndicationFeed()
    {
        using var cts = new CancellationTokenSource(RequestTimeout);
        try
        {
            var items = await FeedReader.ReadAsync(RssFeed, cts.Token);
            return items ?? new Feed();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {RequestTimeout.TotalSeconds:0}s fetching the news feed from {RssFeed}.");
        }
    }
}