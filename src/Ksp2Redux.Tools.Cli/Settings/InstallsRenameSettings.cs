using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that renames a KSP2 install.
/// </summary>
public sealed class InstallsRenameSettings : InstallsTargetSettings
{
    /// <summary>
    /// Gets the new name for the install.
    /// </summary>
    [CommandArgument(1, "<name>")]
    [Description("The new name for the install.")]
    public string Name { get; init; } = "";
}
