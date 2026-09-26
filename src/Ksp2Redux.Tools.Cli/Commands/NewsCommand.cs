using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Lists the latest posts from the KSP2 Redux blog, the feed the launcher's news list shows.
/// </summary>
public sealed class NewsCommand : ReduxCommand<NewsSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        NewsSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Take < 1)
        {
            return context.Output.Fail(ExitCode.USAGE_ERROR, "--take must be at least 1.");
        }

        CodeHollow.FeedReader.Feed feed;
        try
        {
            feed = await context.Output.StatusAsync("Fetching news", _ => context.NewsProvider.GetSyndicationFeed());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return context.Output.Fail(ExitCode.FEED_UNAVAILABLE, $"The news feed could not be fetched: {e.Message}");
        }

        var posts = feed.Items
            .Where(item => item.PublishingDate is not null)
            .OrderByDescending(item => item.PublishingDate)
            .Take(settings.Take)
            .Select(item => new { date = item.PublishingDate!.Value, title = item.Title?.Trim() ?? "", link = item.Link ?? "" })
            .ToList();

        context.Output.Payload(
            posts,
            () => context.Output.Table(
                ["DATE", "TITLE", "LINK"],
                [.. posts.Select(post => (IReadOnlyList<CliCell>)[post.date.ToString("yyyy-MM-dd"), post.title, post.link])]));
        return ExitCode.SUCCESS;
    }
}
