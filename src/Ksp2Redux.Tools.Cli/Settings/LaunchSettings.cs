using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that starts the game.
/// </summary>
public sealed class LaunchSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets a value indicating whether to block until the game exits.
    /// </summary>
    [CommandOption("--wait")]
    [Description("Block until the game exits rather than returning once it starts.")]
    public bool ShouldWait { get; init; }
}
