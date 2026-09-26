using System.ComponentModel;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Settings;

/// <summary>
/// Settings for the command that signs in to Steam.
/// </summary>
public sealed class SteamLoginSettings : SteamSettings
{
    /// <summary>
    /// Gets a value indicating whether to sign in with an account name and password instead of a QR code.
    /// </summary>
    [CommandOption("--password")]
    [Description("Sign in with your account name and password instead of scanning a QR code.")]
    public bool UsePassword { get; init; }

    /// <summary>
    /// Gets the account name for a password sign-in, or null to ask for it.
    /// </summary>
    [CommandOption("--username <NAME>")]
    [Description("Steam account name for --password. Asked for when left out.")]
    public string? Username { get; init; }
}
