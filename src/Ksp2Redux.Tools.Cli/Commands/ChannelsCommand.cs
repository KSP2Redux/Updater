using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Lists the release feeds in the launcher config and the channel each one serves.
/// </summary>
public sealed class ChannelsCommand : ReduxCommand<ChannelsSettings>
{
    /// <inheritdoc />
    // The channel name lives in the manifest rather than the config, so every feed has to be
    // fetched before this can answer. A feed that fails is listed with its error rather than hidden.
    protected override async Task<int> RunAsync(
        CliContext context,
        ChannelsSettings settings,
        CancellationToken cancellationToken)
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
                List<IReadOnlyList<CliCell>> rows =
                [
                    .. loaded.Select(result => (IReadOnlyList<CliCell>)
                    [
                        new CliCell(result.Channel ?? "?", result.IsOk ? null : CliTheme.DETAIL_STYLE),
                        new CliCell(result.IsOk ? "ok" : "FAILED", result.IsOk ? CliTheme.ACTIVE_STYLE : $"bold {CliTheme.DANGER.ToMarkup()}"),
                        result.Feed.Repository, new CliCell(result.Feed.Filename, CliTheme.DETAIL_STYLE),
                        new CliCell(result.Error ?? "", CliTheme.DETAIL_STYLE),
                    ])
                ];

                context.Output.Table(["CHANNEL", "STATUS", "REPOSITORY", "MANIFEST", "ERROR"], rows);
            });

        return loaded.Any(r => r.IsOk) ? ExitCode.SUCCESS : ExitCode.FEED_UNAVAILABLE;
    }
}
