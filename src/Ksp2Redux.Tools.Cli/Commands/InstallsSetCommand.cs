using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Changes an install profile's path and launch settings, the ones the launcher's Settings tab edits.
/// </summary>
public sealed class InstallsSetCommand : ReduxCommand<InstallsSetSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsSetSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings is { Path: null, Arguments: null, ClearArguments: false, GraphicsJobs: null, SteamLaunch: null, SteamAppId: null })
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "Nothing to change. Pass at least one of --path, --args, --clear-args, --graphics-jobs, --steam-launch or --steam-app-id."));
        }

        if (settings is { ClearArguments: true, Arguments: not null })
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "Pass --args or --clear-args, not both."));
        }

        var arguments = settings.ClearArguments ? "" : settings.Arguments?.Trim();

        if (!TryParseSwitch(settings.GraphicsJobs, out var graphicsJobs) || !TryParseSwitch(settings.SteamLaunch, out var steamLaunch))
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "--graphics-jobs and --steam-launch take on or off."));
        }

        var steamAppId = settings.SteamAppId?.Trim();
        if (steamAppId is not null && (steamAppId.Length == 0 || !steamAppId.All(char.IsAsciiDigit)))
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "--steam-app-id must be a number."));
        }

        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        string? exePath = null;
        if (settings.Path is not null)
        {
            exePath = context.ResolveExePath(settings.Path);
            if (context.ReadInstall(exePath) is not { IsValid: true })
            {
                return Task.FromResult(context.Output.Fail(ExitCode.INSTALL_NOT_FOUND, $"{exePath} is not a KSP2 install."));
            }
        }

        var service = context.InstallService;
        if (exePath is not null)
        {
            service.UpdateInstallExePath(entry.Id, exePath);
        }

        if (graphicsJobs is { } jobsOn)
        {
            service.UpdateInstallDisableGraphicsJobs(entry.Id, !jobsOn);
        }

        if (arguments is not null)
        {
            entry.LaunchArguments = arguments;
        }

        if (steamLaunch is { } steamOn)
        {
            entry.LaunchThroughSteam = steamOn;
        }

        if (steamAppId is not null)
        {
            entry.SteamAppId = steamAppId;
        }

        service.NotifyInstallChanged(entry.Id);

        if (!context.ConfigPersisted(config => config.Ksp2Installs.Any(e =>
                e.Id == entry.Id
                && (exePath is null || e.ExePath == exePath)
                && (graphicsJobs is null || e.DisableGraphicsJobs == !graphicsJobs)
                && (arguments is null || e.LaunchArguments == arguments)
                && (steamLaunch is null || e.LaunchThroughSteam == steamLaunch)
                && (steamAppId is null || e.SteamAppId == steamAppId))))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"The changes could not be written to the launcher config at {context.ConfigService.Config.StoragePath}."));
        }

        if (steamLaunch == true && context.OperatingSystemService.IsMacOS())
        {
            context.Output.Warn("Steam cannot run KSP2 on macOS, so launching through Steam is ignored here.");
        }

        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name = entry.Name,
                exePath = entry.ExePath,
                launchArguments = entry.LaunchArguments,
                graphicsJobs = !entry.DisableGraphicsJobs,
                steamLaunch = entry.LaunchThroughSteam,
                steamAppId = entry.SteamAppId,
            },
            () => context.Output.Result(entry.Name));

        return Task.FromResult(ExitCode.SUCCESS);
    }

    /// <summary>
    /// Reads an on or off switch value.
    /// </summary>
    /// <param name="value">The value given, or null when the option was left out.</param>
    /// <param name="parsed">True for on, false for off, null when left out.</param>
    /// <returns>False when a value was given that is neither on nor off.</returns>
    internal static bool TryParseSwitch(string? value, out bool? parsed)
    {
        parsed = value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "on" or "true" or "yes" or "1" => true,
            "off" or "false" or "no" or "0" => false,
            _ => null,
        };

        return value is null || parsed is not null;
    }
}
