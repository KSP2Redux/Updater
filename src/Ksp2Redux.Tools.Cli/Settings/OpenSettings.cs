using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that opens one of the launcher's folders in the file browser.
/// </summary>
public sealed class OpenSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets which folder to open: install, logs, game-data or storage.
    /// </summary>
    [CommandArgument(0, "<folder>")]
    [Description("install (the game folder), logs, game-data (saves and settings) or storage (the launcher's own folder).")]
    public string Folder { get; init; } = "";

    /// <summary>
    /// Gets a value indicating whether the path is printed instead of opened.
    /// </summary>
    [CommandOption("--print")]
    [Description("Print the path instead of opening it.")]
    public bool ShouldPrint { get; init; }
}
