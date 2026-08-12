using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that reports what the launcher can see.
/// </summary>
public sealed class DoctorSettings : BaseSettings
{
    /// <summary>
    /// Gets a value indicating whether the feed check is skipped.
    /// </summary>
    [CommandOption("--offline")]
    [Description("Skip the release feed check, which is the only part that needs the network.")]
    public bool IsOffline { get; init; }
}
