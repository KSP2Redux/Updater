using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that lists the latest KSP2 Redux news.
/// </summary>
public sealed class NewsSettings : BaseSettings
{
    /// <summary>
    /// Gets how many posts to list.
    /// </summary>
    [CommandOption("--take <COUNT>")]
    [Description("How many posts to list, newest first. Defaults to 5.")]
    [DefaultValue(5)]
    public int Take { get; init; } = 5;
}
