using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings shared by the stock bundle conversion commands.
/// </summary>
public abstract class BundlesSettings : BaseSettings
{
    /// <summary>
    /// Gets the KSP2 folder to act on.
    /// </summary>
    // Required rather than defaulting to the active install, because these commands edit stock
    // bundles and that should never happen to an install nobody named.
    [CommandOption("--game <DIR>")]
    [Description("The KSP2 folder to act on, the one holding KSP2_x64.exe. Required.")]
    public string? Game { get; init; }
}
