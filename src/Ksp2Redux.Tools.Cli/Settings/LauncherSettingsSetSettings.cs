using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that changes the launcher-wide settings.
/// </summary>
public sealed class LauncherSettingsSetSettings : LauncherSettingsSettings
{
    /// <summary>
    /// Gets where Redux patches are downloaded from, or null to leave it.
    /// </summary>
    [CommandOption("--patch-source <SOURCE>")]
    [Description("Where Redux patches download from: r2 (the primary service) or github (the permanent backup).")]
    public string? PatchSource { get; init; }

    /// <summary>
    /// Gets how many patch parts download at once, or null to leave it.
    /// </summary>
    [CommandOption("--concurrent-chunks <COUNT>")]
    [Description("How many patch parts download at once, 1 to 8.")]
    public int? ConcurrentChunks { get; init; }

    /// <summary>
    /// Gets whether the launcher writes debug detail to its log file, or null to leave it.
    /// </summary>
    [CommandOption("--verbose-logging <ON_OR_OFF>")]
    [Description("Write more detail to the launcher's log file. Useful when troubleshooting.")]
    public string? VerboseLogging { get; init; }
}
