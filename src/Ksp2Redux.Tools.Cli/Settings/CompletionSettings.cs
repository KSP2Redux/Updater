using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// The shells the CLI can emit a completion script for.
/// </summary>
public enum CompletionShell
{
    /// <summary>
    /// PowerShell 7 and Windows PowerShell.
    /// </summary>
    Pwsh = 0,

    /// <summary>
    /// Bash.
    /// </summary>
    Bash = 1,
}

/// <summary>
/// Settings for the command that prints a shell completion script.
/// </summary>
public sealed class CompletionSettings : CommandSettings
{
    /// <summary>
    /// Gets the shell the script is written for.
    /// </summary>
    [CommandArgument(0, "<shell>")]
    [Description("The shell to emit a script for: pwsh or bash.")]
    public CompletionShell Shell { get; init; }
}
