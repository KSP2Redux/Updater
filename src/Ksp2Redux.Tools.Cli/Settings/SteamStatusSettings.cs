using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that reports the Steam sign-in.
/// </summary>
public sealed class SteamStatusSettings : SteamSettings
{
    /// <summary>
    /// Gets a value indicating whether to connect to Steam and prove the saved login still works.
    /// </summary>
    [CommandOption("--check")]
    [Description("Connect to Steam and check that the saved login still works.")]
    public bool ShouldCheck { get; init; }
}
