using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Lists the versions published to a channel.
/// </summary>
public sealed class ListCommand : ReduxCommand<ListSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        ListSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        var channel = CliContext.ResolveChannel(settings.Channel, entry);
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

        if (settings.Take is { } take && take > 0 && versions.Count > take)
        {
            versions = [.. versions.Skip(versions.Count - take)];
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
        List<IReadOnlyList<CliCell>> rows = [];
        for (var index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            var isNewest = index == versions.Count - 1;
            var style = isNewest ? CliTheme.ACTIVE_STYLE : null;
            rows.Add([
                new CliCell(version.BuildNumber, style),
                new CliCell(CliContext.FormatVersion(version), style),
                new CliCell(version.ReleasedAt?.ToString("u") ?? "", CliTheme.DETAIL_STYLE),
                new CliCell(version.Label ?? "", CliTheme.DETAIL_STYLE),
            ]);
        }

        context.Output.Table(["BUILD", "VERSION", "RELEASED", "LABEL"], rows);
    }
}
