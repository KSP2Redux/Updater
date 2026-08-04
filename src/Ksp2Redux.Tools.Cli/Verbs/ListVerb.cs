using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Lists the versions published to a channel.
/// </summary>
public static class ListVerb
{
    /// <summary>
    /// Prints a channel's versions oldest first, so the newest sits at the bottom of the output.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <param name="options">The parsed options for this verb.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> RunAsync(CliContext context, ListOptions options)
    {
        var entry = context.ResolveInstallEntry(options.Install);
        var channel = CliContext.ResolveChannel(options.Channel, entry);
        if (channel is null)
        {
            return context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "No channel to list. Name a channel, or configure an active install to take one from.");
        }

        var loaded = await context.LoadFeedsAsync();
        if (!context.FeedService.ReleasesFeed.TryGetValue(channel, out var feed))
        {
            return context.FailFeedNotConfigured(channel, loaded);
        }

        // A manifest carries one patch entry per route to a version, normally a full patch and a
        // delta, so the raw enumeration lists most builds twice. Distinct matches how the launcher
        // builds its own version list, keeping the first entry seen for each version.
        var versions = feed.GetAllVersions()
            .Distinct()
            .OrderBy(v => v.ReleasedAt)
            .ToList();

        if (options.Take is { } take && take > 0 && versions.Count > take)
        {
            versions = versions.Skip(versions.Count - take).ToList();
        }

        context.Output.Payload(
            versions.Select(v => new
            {
                version = CliContext.FormatVersion(v),
                buildNumber = v.BuildNumber,
                channel = v.Channel,
                releasedAt = v.ReleasedAt,
                label = v.Label,
            }),
            () => WriteTable(context, versions));

        return ExitCode.SUCCESS;
    }

    private static void WriteTable(CliContext context, IReadOnlyList<GameVersion> versions)
    {
        List<IReadOnlyList<string>> rows = [];
        foreach (var version in versions)
        {
            rows.Add([
                version.BuildNumber,
                CliContext.FormatVersion(version),
                version.ReleasedAt?.ToString("u") ?? "",
                version.Label ?? "",
            ]);
        }

        context.Output.Table(["BUILD", "VERSION", "RELEASED", "LABEL"], rows);
    }
}
