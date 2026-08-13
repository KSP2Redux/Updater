using System.Diagnostics;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Stops a running KSP2, for when the game has hung and will not close on its own.
/// </summary>
public sealed class KillCommand : ReduxCommand<KillSettings>
{
    /// <inheritdoc />
    // Matched by process name rather than by the pid launch reported, because a game started through
    // Steam is not a child of anything this tool ran, and a hung game is often one nobody launched
    // from here at all.
    protected override Task<int> RunAsync(
        CliContext context,
        KillSettings settings,
        CancellationToken cancellationToken)
    {
        var processName = context.FileSystem.Path.GetFileNameWithoutExtension(Ksp2Install.KSP2_EXE_NAME);

        Process[] running;
        try
        {
            running = Process.GetProcessesByName(processName);
        }
        catch (Exception e)
        {
            return Task.FromResult(context.Output.Fail(ExitCode.KILL_FAILED, $"Could not look for {processName}: {e.Message}"));
        }

        // Nothing running is the state the caller wanted, so it is a success rather than a failure.
        if (running.Length == 0)
        {
            context.Output.Heading($"No {processName} process is running.");
            context.Output.Payload(
                new { ok = true, killed = 0, processes = Array.Empty<object>() },
                () => context.Output.Result("0"));

            return Task.FromResult(ExitCode.SUCCESS);
        }

        var found = running.Select(Describe).ToList();

        foreach (var process in found)
        {
            context.Output.Detail($"  pid {process.Pid}  {process.Path ?? "(path unavailable)"}");
        }

        var answer = CliConfirm.Ask(
            context.Output,
            settings.AssumeYes,
            found.Count == 1
                ? $"Stop KSP2 (pid {found[0].Pid})? Anything unsaved is lost."
                : $"Stop {found.Count} KSP2 processes? Anything unsaved is lost.",
            requireAnswer: true);

        switch (answer)
        {
            case ConfirmAnswer.Declined:
                Release(running);
                return Task.FromResult(context.Output.Fail(ExitCode.CANCELLED, "The game is still running."));
            case ConfirmAnswer.NeedsFlag:
                Release(running);
                return Task.FromResult(context.Output.Fail(
                    ExitCode.USAGE_ERROR,
                    "Refusing to stop the game without a terminal to confirm on. Pass --yes."));
            case ConfirmAnswer.Approved:
            default:
                break;
        }

        List<int> killed = [];
        List<string> failures = [];

        foreach (var process in running)
        {
            try
            {
                // Unity spawns children, and killing only the parent leaves them behind holding the
                // game's files open.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(KILL_TIMEOUT_MILLISECONDS);
                killed.Add(process.Id);
            }
            catch (Exception e)
            {
                failures.Add($"pid {process.Id}: {e.Message}");
            }
        }

        Release(running);

        foreach (var failure in failures)
        {
            context.Output.Warn($"Could not stop {failure}");
        }

        context.Output.Payload(
            new
            {
                ok = failures.Count == 0,
                killed = killed.Count,
                pids = killed,
                failures,
            },
            () => context.Output.Result(killed.Count.ToString()));

        return Task.FromResult(failures.Count == 0 ? ExitCode.SUCCESS : ExitCode.KILL_FAILED);
    }

    private const int KILL_TIMEOUT_MILLISECONDS = 10_000;

    // MainModule throws for a process this one cannot open, which includes anything elevated, so the
    // path is reported when it can be and left out when it cannot.
    private static (int Pid, string? Path) Describe(Process process)
    {
        try
        {
            return (process.Id, process.MainModule?.FileName);
        }
        catch (Exception)
        {
            return (process.Id, null);
        }
    }

    private static void Release(Process[] processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
