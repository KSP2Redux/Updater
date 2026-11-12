using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that installs a version.
/// </summary>
public sealed class InstallSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets the version to install, either a full version string or a bare build number.
    /// </summary>
    [CommandArgument(0, "[version]")]
    [Description("Version to install, either 0.2.9.0.103669 or the build number 103669.")]
    public string? Version { get; init; }

    /// <summary>
    /// Gets the channel to resolve the version from, or null to use the install's channel.
    /// </summary>
    [CommandOption("--channel <CHANNEL>")]
    [Description("Channel to resolve the version from. Defaults to the install's configured channel.")]
    public string? Channel { get; init; }

    /// <summary>
    /// Gets the path to a local patch file to apply instead of resolving a published version.
    /// </summary>
    [CommandOption("--patch-file <PATH>")]
    [Description("Apply a local patch file from a stock install instead of resolving a published version.")]
    public string? PatchFile { get; init; }

    /// <summary>
    /// Gets a value indicating whether the plan is printed instead of applied.
    /// </summary>
    [CommandOption("--dry-run")]
    [Description("Print what would be installed and exit without changing anything.")]
    public bool IsDryRun { get; init; }
}
