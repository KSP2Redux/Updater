using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that lists the versions published to a channel.
/// </summary>
public sealed class ListSettings : BaseInstallSettings
{
    /// <summary>
    /// Gets the channel to list, or null to use the install's configured channel.
    /// </summary>
    [CommandOption("--channel <CHANNEL>")]
    [Description("Channel to list. Defaults to the install's configured channel.")]
    public string? Channel { get; init; }

    /// <summary>
    /// Gets the maximum number of versions to print, counting from the newest.
    /// </summary>
    [CommandOption("--take <COUNT>")]
    [Description("Print only the newest N versions.")]
    public int? Take { get; init; }
}
