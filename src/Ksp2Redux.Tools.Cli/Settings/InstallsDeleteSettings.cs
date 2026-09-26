using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that deletes a KSP2 install's game files.
/// </summary>
public sealed class InstallsDeleteSettings : InstallsTargetSettings
{
    /// <summary>
    /// Gets a value indicating whether the confirmation is answered up front.
    /// </summary>
    [CommandOption("--yes|-y")]
    [Description("Delete without asking for confirmation.")]
    public bool AssumeYes { get; init; }
}
