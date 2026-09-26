using System.Diagnostics;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Mac;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Starts KSP2 using an install's configured launch settings.
/// </summary>
public sealed class LaunchCommand : ReduxCommand<LaunchSettings>
{
    private const string DEFAULT_STEAM_APP_ID = "954850";
    private const string GAME_OUTPUT_LOG = "game-output.log";

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        LaunchSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.NoBanner)
        {
            CliBanner.Write(context.Output);
        }

        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return context.FailInstallNotFound(settings.Install);
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

        // Steam on macOS cannot run KSP2, so LaunchThroughSteam is ignored there.
        if (context.OperatingSystemService.IsMacOS())
        {
            return await LaunchThroughWineAsync(context, entry, install, settings, cancellationToken);
        }

        if (entry.LaunchThroughSteam)
        {
            return LaunchThroughSteam(context, entry);
        }

        ProcessStartInfo startInfo = new(install.ExePath) { WorkingDirectory = install.InstallDir };
        if (!string.IsNullOrWhiteSpace(entry.LaunchArguments))
        {
            startInfo.Arguments = entry.LaunchArguments;
        }

        return await StartAndReportAsync(context, startInfo, install.ExePath, settings, null, cancellationToken, null);
    }

    private static async Task<int> LaunchThroughWineAsync(
        CliContext context,
        Ksp2InstallEntry entry,
        Ksp2Install install,
        LaunchSettings settings,
        CancellationToken cancellationToken)
    {
        if (context.WineRuntimeService.Detect() is not { } runtime)
        {
            return context.Output.Fail(
                ExitCode.LAUNCH_FAILED,
                "KSP2 needs a Windows compatibility runtime to run on a Mac, and none was found. " +
                "Install the macOS KSP2 Redux launcher into Applications, which carries one, or install CrossOver.");
        }

        try
        {
            await context.WineRuntimeService.PrepareAsync(runtime, context.Output.Progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return context.Output.Fail(ExitCode.LAUNCH_FAILED, $"Could not set up {runtime.DisplayName}: {e.Message}");
        }

        var logDirectory = context.FileSystem.Path.Combine(context.ConfigService.GetLocalStorageDirectory(), "logs");
        context.FileSystem.Directory.CreateDirectory(logDirectory);
        var gameOutput = context.FileSystem.Path.Combine(logDirectory, GAME_OUTPUT_LOG);
        var startInfo = CliWineProcesses.WithOutputTo(
            context.WineRuntimeService.CreateLaunchInfo(runtime, install.ExePath, install.InstallDir!, entry.LaunchArguments),
            gameOutput);

        var runtimeName = runtime.Kind == WineRuntimeKind.Bundled ? "the launcher's Wine runtime" : "CrossOver";
        return await StartAndReportAsync(context, startInfo, $"{install.ExePath} through {runtimeName}", settings, runtime.DisplayName, cancellationToken,
            $"  Wine and game output goes to {gameOutput}");
    }

    private static async Task<int> StartAndReportAsync(
        CliContext context,
        ProcessStartInfo startInfo,
        string description,
        LaunchSettings settings,
        string? runtime,
        CancellationToken cancellationToken,
        string? detail)
    {
        try
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();
            context.Output.Heading($"Started {description} (pid {process.Id}).");
            if (detail is not null)
            {
                context.Output.Detail(detail);
            }

            if (!settings.ShouldWait)
            {
                context.Output.Payload(
                    new { ok = true, pid = process.Id, waited = false, runtime },
                    () => context.Output.Result(process.Id.ToString()));
                return ExitCode.SUCCESS;
            }

            await process.WaitForExitAsync(cancellationToken);
            context.Output.Payload(
                new { ok = true, pid = process.Id, waited = true, exitCode = process.ExitCode, runtime },
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
    private static int LaunchThroughSteam(CliContext context, Ksp2InstallEntry entry)
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

        context.Output.Heading($"Asked Steam to run app {appId}.");
        context.Output.Payload(
            new { ok = true, steamAppId = appId, waited = false },
            () => context.Output.Result("started"));

        return ExitCode.SUCCESS;
    }
}
