using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that downloads KSP2 from Steam.
/// </summary>
public sealed class SteamDownloadSettings : SteamSettings
{
    /// <summary>
    /// Gets the folder to download into, or null for the default Games folder.
    /// </summary>
    [CommandArgument(0, "[folder]")]
    [Description("Where to put the game. A \"Kerbal Space Program 2\" folder is made inside it. Defaults to ~/Games.")]
    public string? Folder { get; init; }

    /// <summary>
    /// Gets the name for the install profile the download is added as.
    /// </summary>
    [CommandOption("--name <NAME>")]
    [Description("Name for the install profile. Defaults to \"KSP2 (Steam download)\".")]
    public string? Name { get; init; }

    /// <summary>
    /// Gets the release channel for the new install profile, or null for the default.
    /// </summary>
    [CommandOption("--channel <CHANNEL>")]
    [Description("Release channel for the new install profile.")]
    public string? Channel { get; init; }

    /// <summary>
    /// Gets a value indicating whether the confirmation is answered up front.
    /// </summary>
    [CommandOption("--yes|-y")]
    [Description("Download without asking for confirmation.")]
    public bool AssumeYes { get; init; }
}
