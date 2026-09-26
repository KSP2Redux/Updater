using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Shows every setting of one install profile.
/// </summary>
public sealed class InstallsShowCommand : ReduxCommand<InstallsShowSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsShowSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        var install = context.ReadInstall(entry.ExePath);
        var active = context.InstallService.ActiveEntry?.Id == entry.Id;
        var version = install?.GameVersion is null ? null : CliContext.FormatVersion(install.GameVersion);
        var steamLaunchApplies = !context.OperatingSystemService.IsMacOS();

        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name = entry.Name,
                active,
                exePath = entry.ExePath,
                valid = install is { IsValid: true },
                distribution = install?.Distribution.ToString(),
                version,
                channel = entry.ReleaseChannel,
                launchArguments = entry.LaunchArguments,
                graphicsJobs = !entry.DisableGraphicsJobs,
                steamLaunch = entry.LaunchThroughSteam,
                steamAppId = entry.SteamAppId,
            },
            () =>
            {
                context.Output.Result(entry.Name);
                Row(context, "id", entry.Id.ToString());
                Row(context, "active", active ? "yes" : "no");
                Row(context, "path", entry.ExePath);
                Row(context, "state", install is { IsValid: true } ? install.Distribution.ToString() : "not a valid KSP2 install");
                Row(context, "version", version ?? "(unknown)");
                Row(context, "channel", entry.ReleaseChannel);
                var arguments = string.IsNullOrWhiteSpace(entry.LaunchArguments) ? "(none)" : entry.LaunchArguments;
                Row(context, "launch args", steamLaunchApplies && entry.LaunchThroughSteam ? $"{arguments} (not used while launching via Steam)" : arguments);
                Row(context, "graphics jobs", entry.DisableGraphicsJobs ? "off" : "on");
                Row(context, "steam launch", !steamLaunchApplies ? "not used on macOS"
                    : entry.LaunchThroughSteam ? $"on (app {entry.SteamAppId})" : "off");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }

    private static void Row(CliContext context, string label, string value) =>
        context.Output.Result($"  {label + ":",-15}{value}");
}
