using System.Diagnostics;
using Ksp2Redux.Tools.Launcher.Services.Game;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Finds KSP2 and gives it somewhere to write when it runs under Wine on macOS.
/// </summary>
// Under Wine every process is named "wine" to .NET, but ps reports the Windows program as the command.
public static class CliWineProcesses
{
    /// <summary>
    /// Lists the ids of running KSP2 processes, read from ps.
    /// </summary>
    /// <returns>The process ids, empty when the game is not running or ps could not be run.</returns>
    public static IReadOnlyList<int> FindGame() => GameProcessFinder.FindUnderWine();

    /// <summary>
    /// Picks the KSP2 processes out of a ps listing of process ids and command names.
    /// </summary>
    /// <param name="listing">Lines of a pid, whitespace, then the command name.</param>
    /// <returns>The ids of the lines whose command is KSP2_x64.exe, by macOS or Windows path.</returns>
    internal static IReadOnlyList<int> ParseGame(string listing) => GameProcessFinder.ParseWineListing(listing);

    /// <summary>
    /// Wraps a launch so everything the game and Wine print goes to a file rather than the terminal.
    /// </summary>
    /// <param name="launch">The launch to wrap.</param>
    /// <param name="logPath">The file to write, replaced on every launch.</param>
    /// <returns>A launch that runs the same program with the same pid, arguments and environment.</returns>
    // The game outlives the CLI and would keep writing into its terminal. exec keeps the game's pid.
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
