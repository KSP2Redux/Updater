using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Game;

/// <summary>
/// Finds KSP2 running under Wine on macOS or Proton on Linux.
/// </summary>
// Under Wine every process is named "wine" to .NET, but ps reports the Windows program as the command.
public static class GameProcessFinder
{
    /// <summary>
    /// Lists this user's Linux Wine processes whose first Windows argument identifies KSP2.
    /// </summary>
    /// <param name="fileSystem">The filesystem providing the Linux proc directory.</param>
    /// <returns>The game process ids, empty when proc cannot be read or the game is not running.</returns>
    /// <remarks>
    /// Unity renames its main Linux thread, including under Wine. The proc command name therefore cannot identify it.
    /// The executable must be a Wine loader and its first command-line argument must be the game, rather than a later argument.
    /// </remarks>
    public static IReadOnlyList<int> FindUnderProton(IFileSystem fileSystem)
    {
        List<int> found = [];
        try
        {
            uint? owner = ReadEffectiveUser(fileSystem.File.ReadAllText("/proc/self/status"));
            if (owner is null)
                return found;

            foreach (string directory in fileSystem.Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(fileSystem.Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                    continue;
                try
                {
                    if (ReadEffectiveUser(fileSystem.File.ReadAllText(fileSystem.Path.Combine(directory, "status"))) != owner)
                        continue;
                    string? executable = fileSystem.FileInfo.New(fileSystem.Path.Combine(directory, "exe")).LinkTarget;
                    if (executable is null)
                        continue;
                    byte[] commandLine = fileSystem.File.ReadAllBytes(fileSystem.Path.Combine(directory, "cmdline"));
                    if (IsProtonGame(executable, commandLine))
                        found.Add(pid);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Processes can exit during inspection, and another account's process information may be private.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A host without readable proc information cannot identify a Linux Wine process safely.
        }
        return found;
    }

    internal static bool IsProtonGame(string executable, byte[] commandLine)
    {
        const string DELETED_SUFFIX = " (deleted)";
        if (executable.EndsWith(DELETED_SUFFIX, StringComparison.Ordinal))
            executable = executable[..^DELETED_SUFFIX.Length];
        string loader = executable[(executable.LastIndexOf('/') + 1)..];
        if (loader is not ("wine" or "wine64" or "wine-preloader" or "wine64-preloader"))
            return false;

        int end = Array.IndexOf(commandLine, (byte)0);
        if (end <= 0)
            return false;
        string command = Encoding.UTF8.GetString(commandLine, 0, end);
        string name = command[(command.LastIndexOfAny(['/', '\\']) + 1)..];
        return string.Equals(name, Ksp2Install.KSP2_EXE_NAME, StringComparison.OrdinalIgnoreCase);
    }

    private static uint? ReadEffectiveUser(string status)
    {
        foreach (string line in status.Split('\n'))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                continue;
            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 3 && uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint user)
                ? user
                : null;
        }
        return null;
    }

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
