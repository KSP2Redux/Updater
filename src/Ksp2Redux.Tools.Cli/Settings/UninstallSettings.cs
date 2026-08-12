using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that returns an install to stock.
/// </summary>
public sealed class UninstallSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets a value indicating whether the confirmation is answered up front.
    /// </summary>
    [CommandOption("--yes|-y")]
    [Description("Remove without asking for confirmation.")]
    public bool AssumeYes { get; init; }
}
