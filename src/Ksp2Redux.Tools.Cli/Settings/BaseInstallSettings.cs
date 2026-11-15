using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings shared by the commands that act on one KSP2 install.
/// </summary>
public abstract class BaseInstallSettings : BaseSettings
{
    /// <summary>
    /// Gets the id or name of the installation to act on, or null to use the active installation.
    /// </summary>
    [CommandOption("--install <ID_OR_NAME>")]
    [Description("Id or name of the KSP2 install to act on. Defaults to the active install.")]
    public string? Install { get; init; }

    // A blank value would fall back to the active install, so a script with an unset variable would act on it.
    public override ValidationResult Validate() =>
        Install is not null && string.IsNullOrWhiteSpace(Install)
            ? ValidationResult.Error("--install cannot be blank. Leave it out to use the active install.")
            : ValidationResult.Success();
}
