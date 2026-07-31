using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Lists the KSP2 installs in the launcher config.
/// </summary>
public static class InstallsVerb
{
    /// <summary>
    /// Prints every configured install with its id, channel and path.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <returns>Success, or the install not found code when the config lists none.</returns>
    public static Task<int> RunAsync(CliContext context)
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
                List<IReadOnlyList<string>> rows = [];
                foreach (var entry in entries)
                {
                    rows.Add([
                        entry.Id == activeId ? "*" : "",
                        entry.Id.ToString(),
                        entry.Name,
                        entry.ReleaseChannel,
                        entry.ExePath,
                    ]);
                }

                context.Output.Table(["", "ID", "NAME", "CHANNEL", "PATH"], rows);
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
