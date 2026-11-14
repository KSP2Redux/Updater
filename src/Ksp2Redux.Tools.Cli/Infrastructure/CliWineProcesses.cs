using System.Diagnostics;
using System.Text;
using Ksp2Redux.Tools.Launcher.Services.Game;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Finds KSP2 and captures process output from Unix compatibility runtimes.
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
    /// Wraps a launch so everything the game and its runtime print goes to a file rather than the terminal.
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
        const string EXECUTE = "exec \"$0\" \"$@\" > \"$REDUX_GAME_OUTPUT\" 2>&1";
        if (!string.IsNullOrEmpty(launch.Arguments))
        {
            // Process parses the caller's argument string once, just as for an unwrapped launch.
            // The shell receives those arguments as data through "$@", so it never evaluates them.
            wrapped.Arguments = $"-c {QuoteArgument(EXECUTE)} {QuoteArgument(launch.FileName)} {launch.Arguments}";
        }
        else
        {
            wrapped.ArgumentList.Add("-c");
            wrapped.ArgumentList.Add(EXECUTE);
            wrapped.ArgumentList.Add(launch.FileName);
            foreach (var argument in launch.ArgumentList)
            {
                wrapped.ArgumentList.Add(argument);
            }
        }

        foreach (var (key, value) in launch.Environment)
        {
            wrapped.Environment[key] = value;
        }

        wrapped.Environment["REDUX_GAME_OUTPUT"] = logPath;
        return wrapped;
    }

    private static string QuoteArgument(string argument)
    {
        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            quoted.Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2);
        return quoted.Append('"').ToString();
    }
}
