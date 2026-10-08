using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that retargets stock bundles to the replacement shader bundles.
/// </summary>
public sealed class BundlesConvertSettings : BundlesSettings
{
    /// <summary>
    /// Gets the path to the replacement shader bundle manifest.
    /// </summary>
    [CommandOption("--shader-manifest <FILE>")]
    [Description("The manifest.json written next to the Redux replacement shader bundles. Required.")]
    public string? ShaderManifest { get; init; }

    /// <summary>
    /// Gets a value indicating whether the plan is printed instead of carried out.
    /// </summary>
    [CommandOption("--dry-run")]
    [Description("Scan and print the plan without changing anything.")]
    public bool IsDryRun { get; init; }
}
