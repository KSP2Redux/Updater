using System.Diagnostics;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Game;

/// <summary>
/// Finds KSP2 running under Wine on macOS.
/// </summary>
// Under Wine every process is named "wine" to .NET, but ps reports the Windows program as the command.
public static class GameProcessFinder
{
    /// <summary>
    /// Lists the ids of running KSP2 processes, read from ps.
    /// </summary>
    /// <returns>The process ids, empty when the game is not running or ps could not be run.</returns>
    public static IReadOnlyList<int> FindUnderWine()
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
            return ParseWineListing(listing);
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
    public static IReadOnlyList<int> ParseWineListing(string listing)
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
}
