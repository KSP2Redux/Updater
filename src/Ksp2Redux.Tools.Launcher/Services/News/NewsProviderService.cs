using System.Globalization;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.News;

public interface INewsProviderService
{
    Task<Feed> GetSyndicationFeed();
}

/// <summary>
/// Reads the blog feed of the website. The website builds one feed per language that has translated
/// posts, so the launcher asks for the feed of its own language and uses the English one when that
/// feed is missing, empty or unreachable.
/// </summary>
public class NewsProviderService(ILocalizationService localization, ILogService log,
    Func<string, CancellationToken, Task<Feed>>? readFeed = null) : INewsProviderService
{
    // private readonly string _rawRepoTargetUrl = "https://raw.githubusercontent.com/SamBret/LauncherNews/refs/heads/main/";
    // private readonly string _tomlTargetFile = "news.toml";
    // private string _tomlTargetUrl => _rawRepoTargetUrl + _tomlTargetFile;
    // private string _imageTargetUrl => _rawRepoTargetUrl + "images/";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const string SITE = "https://ksp2redux.org";
    internal const string ENGLISH_FEED = SITE + "/blog/rss.xml";

    private readonly Func<string, CancellationToken, Task<Feed>> _readFeed =
        readFeed ?? ((url, token) => FeedReader.ReadAsync(url, token));

    /// <summary>The feed of a language, for example /de/blog/rss.xml and /pt-br/blog/rss.xml.</summary>
    internal static string FeedUrl(CultureInfo culture) =>
        culture.Name.Length == 0 || culture.TwoLetterISOLanguageName == "en"
            ? ENGLISH_FEED
            : $"{SITE}/{culture.Name.ToLowerInvariant()}/blog/rss.xml";

    public async Task<Feed> GetSyndicationFeed()
    {
        var localizedUrl = FeedUrl(localization.Culture);
        if (localizedUrl != ENGLISH_FEED)
        {
            try
            {
                var feed = await Read(localizedUrl);
                if (feed.Items.Count > 0)
                {
                    return feed;
                }

                log.Info($"The news feed {localizedUrl} has no posts, using the English feed.");
            }
            catch (Exception ex)
            {
                log.Warn($"Could not read the news feed {localizedUrl} ({ex.GetType().Name}: {ex.Message}), using the English feed.");
            }
        }

        return await Read(ENGLISH_FEED);
    }

    private async Task<Feed> Read(string url)
    {
        using var cts = new CancellationTokenSource(RequestTimeout);
        try
        {
            return await _readFeed(url, cts.Token) ?? new Feed();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {RequestTimeout.TotalSeconds:0}s fetching the news feed from {url}.");
        }
    }
}
