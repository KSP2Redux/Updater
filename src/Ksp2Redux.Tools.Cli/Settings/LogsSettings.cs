using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that reads the launcher's log files.
/// </summary>
public sealed class LogsSettings : BaseSettings
{
    /// <summary>
    /// Gets a value indicating whether only the path is printed.
    /// </summary>
    [CommandOption("--path")]
    [Description("Print the path to the log file and nothing else.")]
    public bool PathOnly { get; init; }

    /// <summary>
    /// Gets the number of lines to print from the end of the log.
    /// </summary>
    [CommandOption("--tail <LINES>")]
    [Description("Print this many lines from the end of the log. Defaults to 50.")]
    [DefaultValue(50)]
    public int Tail { get; init; }

    /// <summary>
    /// Gets a value indicating whether new lines are printed as they are written.
    /// </summary>
    [CommandOption("--follow|-f")]
    [Description("Keep printing new lines until interrupted.")]
    public bool ShouldFollow { get; init; }
}
