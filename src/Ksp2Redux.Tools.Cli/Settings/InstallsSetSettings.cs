using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that changes an install profile's launch settings.
/// </summary>
public sealed class InstallsSetSettings : InstallsTargetSettings
{
    /// <summary>
    /// Gets the new game folder or executable, or null to leave it.
    /// </summary>
    [CommandOption("--path <PATH>")]
    [Description("Point the profile at another KSP2 folder or KSP2_x64.exe.")]
    public string? Path { get; init; }

    /// <summary>
    /// Gets the new launch arguments, or null to leave them.
    /// </summary>
    [CommandOption("--args <ARGUMENTS>")]
    [Description("Arguments passed to KSP2_x64.exe. Use the --args=\"-popupwindow\" form, as they start with a dash.")]
    public string? Arguments { get; init; }

    /// <summary>
    /// Gets a value indicating whether the launch arguments are removed.
    /// </summary>
    [CommandOption("--clear-args")]
    [Description("Launch KSP2_x64.exe with no arguments.")]
    public bool ClearArguments { get; init; }

    /// <summary>
    /// Gets whether Unity graphics jobs are on or off, or null to leave the setting.
    /// </summary>
    [CommandOption("--graphics-jobs <ON_OR_OFF>")]
    [Description("Turn Unity graphics jobs on or off. Off works around crashes on some hardware.")]
    public string? GraphicsJobs { get; init; }

    /// <summary>
    /// Gets whether the game starts through the Steam client, or null to leave the setting.
    /// </summary>
    [CommandOption("--steam-launch <ON_OR_OFF>")]
    [Description("Start the game through the Steam client. Ignored on macOS.")]
    public string? SteamLaunch { get; init; }

    /// <summary>
    /// Gets the Steam app id to launch, or null to leave it.
    /// </summary>
    [CommandOption("--steam-app-id <ID>")]
    [Description("Steam app id for --steam-launch, for a non-Steam game shortcut. Normally 954850.")]
    public string? SteamAppId { get; init; }
}
