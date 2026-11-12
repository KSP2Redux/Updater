using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Explicit full-source generation and recovery operations for a configured installation.
/// </summary>
public class FullSourceSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets apply, repair, uninstall or recover.
    /// </summary>
    [CommandArgument(0, "<OPERATION>")]
    [Description("apply, repair, uninstall or recover.")]
    public string Operation { get; init; } = "";

    /// <summary>
    /// Gets the authenticated release selector for apply or repair.
    /// </summary>
    [CommandArgument(1, "[RELEASE]")]
    public string? Release { get; init; }

    /// <summary>
    /// Gets the release channel, or null for the configured channel.
    /// </summary>
    [CommandOption("--channel <CHANNEL>")]
    public string? Channel { get; init; }

    /// <summary>
    /// Gets whether an uninstall confirmation has already been supplied.
    /// </summary>
    [CommandOption("-y|--yes")]
    public bool AssumeYes { get; init; }
}
