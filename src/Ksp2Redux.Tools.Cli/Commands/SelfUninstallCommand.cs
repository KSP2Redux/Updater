using System.Diagnostics;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Removes the CLI from this machine.
/// </summary>
public sealed class SelfUninstallCommand : ReduxCommand<SelfUninstallSettings>
{
    private const string PATH_VARIABLE = "PATH";

    /// <inheritdoc />
    protected override bool NoticeApplies => false;

    /// <inheritdoc />
    // The launcher config and the log folder are shared with the launcher window, so neither is
    // touched here. This removes the CLI binary and the PATH entry the installer added, nothing else.
    protected override Task<int> RunAsync(
        CliContext context,
        SelfUninstallSettings settings,
        CancellationToken cancellationToken)
    {
        var executable = context.EnvironmentProvider.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !context.FileSystem.File.Exists(executable))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.SELF_UPDATE_FAILED,
                "Could not work out which file is running, so there is nothing to remove."));
        }

        var directory = context.FileSystem.Path.GetDirectoryName(executable) ?? "";

        var answer = CliConfirm.Ask(
            context.Output,
            settings.AssumeYes,
            $"Remove {executable}? Your launcher config and logs are left alone.",
            requireAnswer: true);

        switch (answer)
        {
            case ConfirmAnswer.Declined:
                return Task.FromResult(context.Output.Fail(ExitCode.CANCELLED, "Nothing was removed."));
            case ConfirmAnswer.NeedsFlag:
                return Task.FromResult(context.Output.Fail(
                    ExitCode.USAGE_ERROR,
                    "Refusing to remove the running binary without a terminal to confirm on. Pass --yes."));
            case ConfirmAnswer.Approved:
            default:
                break;
        }

        var pathRemoved = RemoveFromUserPath(context, directory);

        try
        {
            if (context.OperatingSystemService.IsLinux())
            {
                context.FileSystem.File.Delete(executable);
            }
            else
            {
                ScheduleWindowsDelete(context, executable, directory);
            }
        }
        catch (Exception e)
        {
            return Task.FromResult(context.Output.Fail(ExitCode.SELF_UPDATE_FAILED, $"Could not remove {executable}: {e.Message}"));
        }

        context.Output.Payload(
            new
            {
                ok = true,
                removed = executable,
                pathEntryRemoved = pathRemoved,
                configKept = context.ConfigService.Config.StoragePath,
            },
            () =>
            {
                context.Output.Result(executable);
                context.Output.Detail(context.OperatingSystemService.IsLinux()
                    ? "  the binary is gone"
                    : "  the binary is removed once this process exits");

                context.Output.Detail(pathRemoved
                    ? "  the install folder was taken off your PATH, open a new terminal for that to apply"
                    : "  no PATH entry to remove");

                context.Output.Detail($"  your launcher config is untouched at {context.ConfigService.Config.StoragePath}");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }

    // Only the user scoped PATH is touched, which is the one the install script writes to. A machine
    // scoped entry would need elevation and the installer never creates one.
    private static bool RemoveFromUserPath(CliContext context, string directory)
    {
        if (context.OperatingSystemService.IsLinux() || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var path = context.EnvironmentVariables.GetEnvironmentVariable(PATH_VARIABLE, EnvironmentVariableTarget.User) ?? "";
            var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kept = entries
                .Where(entry => !string.Equals(entry.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (kept.Count == entries.Length)
            {
                return false;
            }

            context.EnvironmentVariables.SetEnvironmentVariable(
                PATH_VARIABLE,
                string.Join(';', kept),
                EnvironmentVariableTarget.User);

            return true;
        }
        catch (Exception e)
        {
            context.LogService.Warn($"Could not take {directory} off the user PATH: {e.Message}");
            return false;
        }
    }

    // Windows holds a lock on a running executable, so the delete is handed to a detached shell that
    // waits for this process to go away first. The folder goes too, but only if nothing else is in it.
    private static void ScheduleWindowsDelete(CliContext context, string executable, string directory)
    {
        var command = $"""
            timeout /t 2 /nobreak >nul & del /f /q "{executable}" "{executable}.old" 2>nul & rmdir "{directory}" 2>nul
            """;

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process.Start(startInfo);
        context.LogService.Info($"Scheduled removal of {executable} after this process exits.");
    }
}
