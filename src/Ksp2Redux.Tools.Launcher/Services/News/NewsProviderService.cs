using CodeHollow.FeedReader;

namespace Ksp2Redux.Tools.Launcher.Services.News;

public interface INewsProviderService
{
    Task<Feed> GetSyndicationFeed();
}

/// <summary>
/// Reads the blog feed of the website. The site has a single feed, in English, for every language.
/// </summary>
public class NewsProviderService(Func<string, CancellationToken, Task<Feed>>? readFeed = null) : INewsProviderService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const string FEED = "https://ksp2redux.org/blog/rss.xml";

    private readonly Func<string, CancellationToken, Task<Feed>> _readFeed =
        readFeed ?? ((url, token) => FeedReader.ReadAsync(url, token));

    public async Task<Feed> GetSyndicationFeed()
    {
        using var cts = new CancellationTokenSource(RequestTimeout);
        try
        {
            return await _readFeed(FEED, cts.Token) ?? new Feed();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {RequestTimeout.TotalSeconds:0}s fetching the news feed from {FEED}.");
        }
    }
}
