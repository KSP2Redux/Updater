using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public interface IGameDataFolderService
{
    /// <summary>
    /// Works out where KSP2 keeps its saves, settings and its own log file.
    /// </summary>
    /// <param name="entry">The install to resolve it for, used on Linux to find the Proton prefix.</param>
    /// <returns>The folder path, which may not exist yet, or null when it cannot be worked out.</returns>
    string? Resolve(Ksp2InstallEntry? entry);
}

/// <summary>
/// Finds the folder Unity gives KSP2 for persistent data: saves, settings, screenshots and Player.log.
/// </summary>
// On Windows this is LocalLow, which has no SpecialFolder of its own, so it is built off the user
// profile. On Linux the game runs under Proton, so the same folder lives inside the Steam prefix
// beside the install rather than anywhere in the real home directory.
public class GameDataFolderService(
    IFileSystem fileSystem,
    IEnvironmentProvider environmentProvider,
    IOperatingSystemService operatingSystemService) : IGameDataFolderService
{
    private const string PUBLISHER_FOLDER = "Intercept Games";
    private const string GAME_FOLDER = "Kerbal Space Program 2";
    private const string STEAM_APPS_FOLDER = "steamapps";

    public string? Resolve(Ksp2InstallEntry? entry)
    {
        return operatingSystemService.IsLinux() ? ResolveUnderProton(entry) : ResolveOnWindows();
    }

    private string ResolveOnWindows()
    {
        var profile = environmentProvider.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return fileSystem.Path.Combine(profile, "AppData", "LocalLow", PUBLISHER_FOLDER, GAME_FOLDER);
    }

    // A Proton prefix sits next to the install: steamapps/common/<game> alongside
    // steamapps/compatdata/<appid>/pfx, and inside the prefix the game sees a normal Windows profile.
    // An install that is not under a steamapps folder has no prefix to look in.
    private string? ResolveUnderProton(Ksp2InstallEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(entry?.ExePath) || string.IsNullOrWhiteSpace(entry.SteamAppId))
        {
            return null;
        }

        var steamApps = FindSteamAppsFolder(entry.ExePath);
        if (steamApps is null)
        {
            return null;
        }

        return fileSystem.Path.Combine(
            steamApps,
            "compatdata",
            entry.SteamAppId,
            "pfx",
            "drive_c",
            "users",
            "steamuser",
            "AppData",
            "LocalLow",
            PUBLISHER_FOLDER,
            GAME_FOLDER);
    }

    private string? FindSteamAppsFolder(string exePath)
    {
        var directory = fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(exePath));

        while (!string.IsNullOrEmpty(directory))
        {
            if (string.Equals(fileSystem.Path.GetFileName(directory), STEAM_APPS_FOLDER, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            directory = fileSystem.Path.GetDirectoryName(directory);
        }

        return null;
    }
}
