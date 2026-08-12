using System.ComponentModel;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings shared by every command.
/// </summary>
public abstract class BaseSettings : CommandSettings
{
    /// <summary>
    /// Gets a value indicating whether stdout carries a JSON document rather than text.
    /// </summary>
    [CommandOption("--json")]
    [Description("Emit a JSON document on stdout instead of text.")]
    public bool IsJson { get; init; }

    /// <summary>
    /// Gets a value indicating whether the launcher's own log lines are printed.
    /// </summary>
    [CommandOption("--verbose")]
    [Description("Print the launcher's info and debug log lines to stderr.")]
    public bool IsVerbose { get; init; }

    /// <summary>
    /// Gets a value indicating whether the Redux logo is suppressed.
    /// </summary>
    [CommandOption("--no-banner")]
    [Description("Do not draw the Redux logo.")]
    public bool NoBanner { get; init; }

    /// <summary>
    /// Gets a value indicating whether progress, headings and detail lines are suppressed.
    /// </summary>
    [CommandOption("--quiet")]
    [Description("Suppress progress on stderr. Warnings and errors are still printed.")]
    public bool IsQuiet { get; init; }

    /// <summary>
    /// Gets when color, tables, and progress bars are drawn.
    /// </summary>
    [CommandOption("--color <MODE>")]
    [Description("When to style output: auto, always or never. Defaults to auto.")]
    [DefaultValue(ColorMode.Auto)]
    public ColorMode Color { get; init; }
}
