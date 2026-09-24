using System.Diagnostics;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Finds KSP2 and gives it somewhere to write when it runs under Wine on macOS.
/// </summary>
// Under Wine every process is named "wine" as far as .NET can see, so looking the game up by name
// finds nothing. Wine does set each process's command name to the Windows program it runs, which ps
// reports, so that is what the game is found by.
public static class CliWineProcesses
{
    /// <summary>
    /// Lists the ids of running KSP2 processes, read from ps.
    /// </summary>
    /// <returns>The process ids, empty when the game is not running or ps could not be run.</returns>
    public static IReadOnlyList<int> FindGame()
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("/bin/ps", "-axo pid=,comm=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (ps is null)
            {
                return [];
            }

            var listing = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            return ParseGame(listing);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Picks the KSP2 processes out of a ps listing of process ids and command names.
    /// </summary>
    /// <param name="listing">Lines of a pid, whitespace, then the command name.</param>
    /// <returns>The ids of the lines whose command is KSP2_x64.exe, by macOS or Windows path.</returns>
    internal static IReadOnlyList<int> ParseGame(string listing)
    {
        List<int> found = [];
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0 || !int.TryParse(line[..split], out var pid))
            {
                continue;
            }

            var command = line[split..].Trim();
            var name = command[(command.LastIndexOfAny(['/', '\\']) + 1)..];
            if (string.Equals(name, Ksp2Install.KSP2_EXE_NAME, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(pid);
            }
        }

        return found;
    }

    /// <summary>
    /// Wraps a launch so everything the game and Wine print goes to a file rather than the terminal.
    /// </summary>
    /// <param name="launch">The launch to wrap.</param>
    /// <param name="logPath">The file to write, replaced on every launch.</param>
    /// <returns>A launch that runs the same program with the same pid, arguments and environment.</returns>
    // The game outlives the CLI that started it, and without this Unity and DXMT keep writing into
    // whatever terminal that was, long after its prompt came back. exec keeps the pid the game's own.
    public static ProcessStartInfo WithOutputTo(ProcessStartInfo launch, string logPath)
    {
        ProcessStartInfo wrapped = new("/bin/sh")
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
        };
        wrapped.ArgumentList.Add("-c");
        wrapped.ArgumentList.Add("exec \"$0\" \"$@\" > \"$REDUX_GAME_OUTPUT\" 2>&1");
        wrapped.ArgumentList.Add(launch.FileName);
        foreach (var argument in launch.ArgumentList)
        {
            wrapped.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in launch.Environment)
        {
            wrapped.Environment[key] = value;
        }

        wrapped.Environment["REDUX_GAME_OUTPUT"] = logPath;
        return wrapped;
    }
}
