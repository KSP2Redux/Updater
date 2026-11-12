using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that adds a KSP2 install to the launcher config.
/// </summary>
public sealed class InstallsAddSettings : InstallsSettings
{
    /// <summary>
    /// Gets the KSP2 folder or executable to add, or null to use the detected one.
    /// </summary>
    [CommandArgument(0, "[path]")]
    [Description("Path to the KSP2 folder or to KSP2_x64.exe. Defaults to the detected install.")]
    public string? Path { get; init; }

    /// <summary>
    /// Gets the name to give the install, or null to derive one from the path.
    /// </summary>
    [CommandOption("--name <NAME>")]
    [Description("Name for the install. Defaults to a name derived from the folder.")]
    public string? Name { get; init; }

    /// <summary>
    /// Gets the release channel to put the install on, or null to inherit the default.
    /// </summary>
    [CommandOption("--channel <CHANNEL>")]
    [Description("Release channel for the install. Defaults to the channel the launcher would pick.")]
    public string? Channel { get; init; }

    /// <summary>
    /// Gets a value indicating whether the new install becomes the active one.
    /// </summary>
    [CommandOption("--activate")]
    [Description("Make the new install the active one.")]
    public bool ShouldActivate { get; init; }
}
