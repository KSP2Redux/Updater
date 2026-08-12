using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that replaces the CLI with a newer build.
/// </summary>
public sealed class SelfUpdateSettings : BaseSettings
{
    /// <summary>
    /// Gets a value indicating whether the update is only reported rather than installed.
    /// </summary>
    [CommandOption("--check")]
    [Description("Report whether an update is available and exit without installing it.")]
    public bool CheckOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether the confirmation is answered up front.
    /// </summary>
    [CommandOption("--yes|-y")]
    [Description("Install without asking for confirmation.")]
    public bool AssumeYes { get; init; }
}
