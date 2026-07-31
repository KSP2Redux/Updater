using CommandLine;

namespace Ksp2Redux.Tools.Cli;

/// <summary>
/// Options shared by every verb.
/// </summary>
public abstract class BaseOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether stdout carries a JSON document rather than text.
    /// </summary>
    [Option("json", Required = false, HelpText = "Emit a JSON document on stdout instead of text.")]
    public bool IsJson { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the launcher's own log lines are printed.
    /// </summary>
    [Option("verbose", Required = false, HelpText = "Print the launcher's info and debug log lines to stderr.")]
    public bool IsVerbose { get; set; }
}

/// <summary>
/// Options shared by the verbs that act on one KSP2 install.
/// </summary>
public abstract class BaseInstallOptions : BaseOptions
{
    /// <summary>
    /// Gets or sets the id or name of the install to act on, or null to use the active install.
    /// </summary>
    [Option("install", Required = false, HelpText = "Id or name of the KSP2 install to act on. Defaults to the active install.")]
    public string? Install { get; set; }
}

/// <summary>
/// Options for the verb that lists the configured release feeds.
/// </summary>
[Verb("channels", HelpText = "List the release feeds in the launcher config and the channel each one serves.")]
public sealed class ChannelsOptions : BaseOptions;

/// <summary>
/// Options for the verb that lists the configured KSP2 installs.
/// </summary>
[Verb("installs", aliases: ["list-installs"], HelpText = "List the KSP2 installs in the launcher config.")]
public sealed class InstallsOptions : BaseOptions;

/// <summary>
/// Options for the verb that lists the versions published to a channel.
/// </summary>
[Verb("list", HelpText = "List the versions published to a channel, oldest first.")]
public sealed class ListOptions : BaseInstallOptions
{
    /// <summary>
    /// Gets or sets the channel to list, or null to use the install's configured channel.
    /// </summary>
    [Option("channel", Required = false, HelpText = "Channel to list. Defaults to the install's configured channel.")]
    public string? Channel { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of versions to print, counting from the newest.
    /// </summary>
    [Option("take", Required = false, HelpText = "Print only the newest N versions.")]
    public int? Take { get; set; }
}

/// <summary>
/// Options for the verb that reports the version currently installed.
/// </summary>
[Verb("current", HelpText = "Report the Redux version currently installed into a KSP2 install.")]
public sealed class CurrentOptions : BaseInstallOptions;

/// <summary>
/// Options for the verb that installs a version.
/// </summary>
[Verb("install", HelpText = "Install a specific version into a KSP2 install.")]
public sealed class InstallOptions : BaseInstallOptions
{
    /// <summary>
    /// Gets or sets the version to install, either a full version string or a bare build number.
    /// </summary>
    [Value(0, MetaName = "version", Required = false, HelpText = "Version to install, either 0.2.9.0.103669 or the build number 103669.")]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the channel to resolve the version from, or null to use the install's channel.
    /// </summary>
    [Option("channel", Required = false, HelpText = "Channel to resolve the version from. Defaults to the install's configured channel.")]
    public string? Channel { get; set; }

    /// <summary>
    /// Gets or sets the path to a local patch file to apply instead of resolving a published version.
    /// </summary>
    [Option("patch-file", Required = false, HelpText = "Apply a local patch file from a stock install instead of resolving a published version.")]
    public string? PatchFile { get; set; }
}

/// <summary>
/// Options for the verb that returns an install to stock.
/// </summary>
[Verb("uninstall", HelpText = "Remove Redux from a KSP2 install, returning it to stock.")]
public sealed class UninstallOptions : BaseInstallOptions;

/// <summary>
/// Options for the verb that starts the game.
/// </summary>
[Verb("launch", HelpText = "Start KSP2 using the install's configured launch settings.")]
public sealed class LaunchOptions : BaseInstallOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to block until the game exits.
    /// </summary>
    [Option("wait", Required = false, HelpText = "Block until the game exits rather than returning once it starts.")]
    public bool ShouldWait { get; set; }
}
