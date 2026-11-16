using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the installs subcommands that name the install to act on positionally.
/// </summary>
public abstract class InstallsTargetSettings : InstallsSettings
{
    /// <summary>
    /// Gets the id or name of the install to act on.
    /// </summary>
    [CommandArgument(0, "<install>")]
    [Description("Id or name of the KSP2 install to act on.")]
    public string Install { get; init; } = "";

    /// <inheritdoc />
    // A blank selector means "the active install" further down, so a script passing an unset
    // variable would otherwise act on, or delete, whichever install happens to be active.
    public override ValidationResult Validate() =>
        string.IsNullOrWhiteSpace(Install)
            ? ValidationResult.Error("The install to act on cannot be blank. Name it by id or name.")
            : ValidationResult.Success();
}
