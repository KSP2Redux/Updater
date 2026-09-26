using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that shows every setting of one install profile.
/// </summary>
public sealed class InstallsShowSettings : InstallsSettings
{
    /// <summary>
    /// Gets the id or name of the install to show, or null for the active one.
    /// </summary>
    [CommandArgument(0, "[install]")]
    [Description("Id or name of the KSP2 install to show. Defaults to the active install.")]
    public string? Install { get; init; }
}
