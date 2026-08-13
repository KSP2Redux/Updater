namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Process exit codes returned by the CLI commands.
/// </summary>
// Scripts branch on these values, so the numbers are part of the contract. Append new members
// rather than renumbering existing ones.
public static class ExitCode
{
    /// <summary>
    /// The command completed.
    /// </summary>
    public const int SUCCESS = 0;

    /// <summary>
    /// Argument parsing failed, or a command was given arguments it cannot satisfy.
    /// </summary>
    public const int USAGE_ERROR = 1;

    /// <summary>
    /// The requested channel has no feed in the launcher config.
    /// </summary>
    // Separate from every other failure because the fix is a documented one time setup step rather
    // than anything about the command that was run.
    public const int FEED_NOT_CONFIGURED = 2;

    /// <summary>
    /// A feed is configured but its manifest could not be downloaded or parsed.
    /// </summary>
    public const int FEED_UNAVAILABLE = 3;

    /// <summary>
    /// No KSP2 install matched, or the matched install is not a valid KSP2 directory.
    /// </summary>
    public const int INSTALL_NOT_FOUND = 4;

    /// <summary>
    /// The requested version or build number is not present in the resolved channel.
    /// </summary>
    public const int VERSION_NOT_FOUND = 5;

    /// <summary>
    /// Planning or applying the install failed.
    /// </summary>
    public const int INSTALL_FAILED = 6;

    /// <summary>
    /// Starting the game failed.
    /// </summary>
    public const int LAUNCH_FAILED = 7;

    /// <summary>
    /// The command was cancelled before it finished.
    /// </summary>
    public const int CANCELLED = 8;

    /// <summary>
    /// The launcher config could not be written, so the change was not kept.
    /// </summary>
    public const int CONFIG_WRITE_FAILED = 9;

    /// <summary>
    /// The CLI could not replace or remove its own binary.
    /// </summary>
    public const int SELF_UPDATE_FAILED = 10;

    /// <summary>
    /// A running game was found but could not be stopped.
    /// </summary>
    public const int KILL_FAILED = 11;
}
