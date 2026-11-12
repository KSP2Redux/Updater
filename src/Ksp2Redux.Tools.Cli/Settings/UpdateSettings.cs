using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that installs the newest version in a channel.
/// </summary>
public sealed class UpdateSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets the channel to take the newest version from, or null to use the install's channel.
    /// </summary>
    [CommandOption("--channel <CHANNEL>")]
    [Description("Channel to update from. Defaults to the install's configured channel.")]
    public string? Channel { get; init; }

    /// <summary>
    /// Gets a value indicating whether the plan is printed instead of applied.
    /// </summary>
    [CommandOption("--dry-run")]
    [Description("Print what would be installed and exit without changing anything.")]
    public bool IsDryRun { get; init; }
}
