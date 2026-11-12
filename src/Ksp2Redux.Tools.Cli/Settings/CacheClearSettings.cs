using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that empties the download cache.
/// </summary>
public sealed class CacheClearSettings : CacheSettings
{
    /// <summary>
    /// Gets the age in days a file has to reach before it is deleted, or null to delete everything.
    /// </summary>
    [CommandOption("--older-than <DAYS>")]
    [Description("Only delete files last written more than this many days ago.")]
    public int? OlderThanDays { get; init; }

    /// <summary>
    /// Gets a value indicating whether the confirmation is answered up front.
    /// </summary>
    [CommandOption("--yes|-y")]
    [Description("Delete without asking for confirmation.")]
    public bool AssumeYes { get; init; }
}
