using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that reports the CLI's own version.
/// </summary>
public sealed class VersionSettings : BaseSettings
{
    /// <summary>
    /// Gets a value indicating whether the newest published release is looked up.
    /// </summary>
    [CommandOption("--check")]
    [Description("Also report whether a newer release is published.")]
    public bool ShouldCheck { get; init; }
}
