using System.Diagnostics;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Starts KSP2 using an install's configured launch settings.
/// </summary>
public static class LaunchVerb
{
    private const string DEFAULT_STEAM_APP_ID = "954850";

    /// <summary>
    /// Starts the game, either directly or through Steam, depending on the install's settings.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <param name="options">The parsed options for this verb.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> RunAsync(CliContext context, LaunchOptions options, CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(options.Install);
        if (entry is null)
        {
            return context.FailInstallNotFound(options.Install);
        }

        var install = context.InstallService.Ksp2;
        if (install is not { IsValid: true })
        {
            return context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"'{entry.Name}' does not point at a valid KSP2 install: {entry.ExePath}");
        }

        // Writes the graphics jobs setting the install was configured with. The launcher does this
        // on startup, so a CLI launch has to do it too or the setting silently does not apply.
        context.InstallService.ApplyActiveInstallBootConfig();

        if (entry.LaunchThroughSteam)
        {
            return LaunchThroughSteam(context, entry);
        }

        try
        {
            using Process process = new();
            process.StartInfo.FileName = install.ExePath;
            process.StartInfo.WorkingDirectory = install.InstallDir;
            if (!string.IsNullOrWhiteSpace(entry.LaunchArguments))
            {
                process.StartInfo.Arguments = entry.LaunchArguments;
            }

            process.Start();
            context.Output.Progress($"Started {install.ExePath} (pid {process.Id}).");

            if (!options.ShouldWait)
            {
                context.Output.Payload(
                    new { ok = true, pid = process.Id, waited = false },
                    () => context.Output.Result(process.Id.ToString()));
                return ExitCode.SUCCESS;
            }

            await process.WaitForExitAsync(cancellationToken);
            context.Output.Payload(
                new { ok = true, pid = process.Id, waited = true, exitCode = process.ExitCode },
                () => context.Output.Result(process.ExitCode.ToString()));

            return ExitCode.SUCCESS;
        }
        catch (OperationCanceledException)
        {
            return context.Output.Fail(ExitCode.CANCELLED, "Stopped waiting for the game to exit. The game is still running.");
        }
        catch (Exception e)
        {
            return context.Output.Fail(
                ExitCode.LAUNCH_FAILED,
                $"Could not start the game: {e.Message} It may have been moved, removed, or blocked by antivirus software.");
        }
    }

    // Steam owns the process it starts, so there is no pid to report and nothing to wait on here.
    private static int LaunchThroughSteam(CliContext context, Ksp2Redux.Tools.Launcher.Models.Ksp2InstallEntry entry)
    {
        var appId = string.IsNullOrWhiteSpace(entry.SteamAppId) ? DEFAULT_STEAM_APP_ID : entry.SteamAppId;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{appId}",
                UseShellExecute = true,
            };
            Process.Start(startInfo);
        }
        catch (Exception e)
        {
            return context.Output.Fail(
                ExitCode.LAUNCH_FAILED,
                $"Could not open Steam: {e.Message} Make sure Steam is installed and try again.");
        }

        context.Output.Progress($"Asked Steam to run app {appId}.");
        context.Output.Payload(
            new { ok = true, steamAppId = appId, waited = false },
            () => context.Output.Result("started"));

        return ExitCode.SUCCESS;
    }
}
