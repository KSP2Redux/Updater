using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that stops a running KSP2.
/// </summary>
public sealed class KillSettings : BaseSettings
{
    /// <summary>
    /// Gets a value indicating whether the confirmation is answered up front.
    /// </summary>
    [CommandOption("--yes|-y")]
    [Description("Stop the game without asking for confirmation.")]
    public bool AssumeYes { get; init; }
}
