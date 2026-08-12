using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that moves a KSP2 install to another release channel.
/// </summary>
public sealed class InstallsChannelSettings : InstallsTargetSettings
{
    /// <summary>
    /// Gets the channel the install should follow.
    /// </summary>
    [CommandArgument(1, "<channel>")]
    [Description("The release channel the install should follow, such as stable or beta.")]
    public string Channel { get; init; } = "";
}
