using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Lists the KSP2 installs in the launcher config.
/// </summary>
public sealed class InstallsCommand : ReduxCommand<InstallsSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsSettings settings,
        CancellationToken cancellationToken)
    {
        context.InstallService.TryLoadKsp2Install();

        var entries = context.InstallService.Entries;
        if (entries.Count == 0)
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                "The launcher config lists no KSP2 installs. Add one in the launcher first."));
        }

        var activeId = context.InstallService.ActiveEntry?.Id;

        context.Output.Payload(
            entries.Select(e => new
            {
                id = e.Id,
                name = e.Name,
                exePath = e.ExePath,
                channel = e.ReleaseChannel,
                active = e.Id == activeId,
                lastInstalledVersion = e.LastInstalledVersion is null ? null : CliContext.FormatVersion(e.LastInstalledVersion),
            }),
            () =>
            {
                List<IReadOnlyList<CliCell>> rows = [];
                foreach (var entry in entries)
                {
                    var isActive = entry.Id == activeId;
                    var nameStyle = isActive ? CliTheme.ACTIVE_STYLE : null;
                    rows.Add([
                        new CliCell(isActive ? "*" : "", nameStyle),
                        new CliCell(ShortId(context, entry.Id), CliTheme.DETAIL_STYLE),
                        new CliCell(entry.Name, nameStyle),
                        entry.ReleaseChannel,
                        CliCell.Path(entry.ExePath),
                    ]);
                }

                context.Output.Table(["", "ID", "NAME", "CHANNEL", "PATH"], rows);
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }

    // A full id is 36 characters of a table that also has to hold a path. The short form is enough
    // to identify an install and enough to pass back in, because a prefix resolves. Plain output and
    // the JSON document keep the whole thing.
    private static string ShortId(CliContext context, Guid id) =>
        context.Output.Capabilities.FancyResults
            ? id.ToString()[..CliContext.ID_PREFIX_LENGTH]
            : id.ToString();
}
