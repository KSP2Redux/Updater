using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings shared by the commands that act on one KSP2 install.
/// </summary>
public abstract class BaseInstallSettings : BaseSettings
{
    /// <summary>
    /// Gets the id or name of the installation to act on, or null to use the active installation.
    /// </summary>
    [CommandOption("--install <ID_OR_NAME>")]
    [Description("Id or name of the KSP2 install to act on. Defaults to the active install.")]
    public string? Install { get; init; }
}
