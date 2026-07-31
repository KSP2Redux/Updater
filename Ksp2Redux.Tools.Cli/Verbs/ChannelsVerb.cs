using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Lists the release feeds in the launcher config and the channel each one serves.
/// </summary>
public static class ChannelsVerb
{
    /// <summary>
    /// Loads every configured feed and reports the channel it declares.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <returns>Success, or the feed unavailable code when no feed loaded at all.</returns>
    // The channel name lives in the manifest rather than the config, so every feed has to be
    // fetched before this can answer. A feed that fails is listed with its error rather than hidden.
    public static async Task<int> RunAsync(CliContext context)
    {
        var loaded = await context.LoadFeedsAsync();

        if (loaded.Count == 0)
        {
            return context.Output.Fail(ExitCode.FEED_NOT_CONFIGURED, "The launcher config lists no feeds.");
        }

        context.Output.Payload(
            loaded.Select(r => new
            {
                channel = r.Channel,
                repository = r.Feed.Repository,
                filename = r.Feed.Filename,
                ok = r.IsOk,
                error = r.Error,
            }),
            () =>
            {
                List<IReadOnlyList<string>> rows = [];
                foreach (var result in loaded)
                {
                    rows.Add([
                        result.Channel ?? "?",
                        result.IsOk ? "ok" : "FAILED",
                        result.Feed.Repository,
                        result.Feed.Filename,
                        result.Error ?? "",
                    ]);
                }

                context.Output.Table(["CHANNEL", "STATUS", "REPOSITORY", "MANIFEST", "ERROR"], rows);
            });

        return loaded.Any(r => r.IsOk) ? ExitCode.SUCCESS : ExitCode.FEED_UNAVAILABLE;
    }
}
