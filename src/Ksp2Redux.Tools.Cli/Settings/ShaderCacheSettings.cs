using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for maintaining the finished shader bundle cache shared with Unity.
/// </summary>
public sealed class ShaderCacheSettings : BaseSettings
{
    /// <summary>
    /// Gets a value indicating whether expired managed entries should be removed.
    /// </summary>
    [CommandOption("--prune")]
    [Description("Remove managed shader bundles unused for the configured idle lifetime.")]
    public bool Prune { get; init; }
}
