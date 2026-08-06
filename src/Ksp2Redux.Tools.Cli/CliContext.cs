using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Microsoft.Extensions.DependencyInjection;

namespace Ksp2Redux.Tools.Cli;

/// <summary>
/// The setup every verb shares: the launcher config, the KSP2 install to act on, and the release feeds.
/// </summary>
public sealed class CliContext
{
    private const string DOWNLOAD_CACHE_FOLDER = "download-cache";
    private const string INTERNAL_CHANNEL = "internal";

    private readonly IFileSystem _fileSystem;
    private readonly IManifestReleasesFeedProviderService _feedProvider;
    private readonly ILogService _log;

    /// <summary>
    /// Initializes the context by resolving the launcher services the verbs need.
    /// </summary>
    /// <param name="services">The container built by <see cref="CliServiceProvider" />.</param>
    /// <param name="output">The writer the verbs report through.</param>
    public CliContext(IServiceProvider services, CliOutput output)
    {
        Output = output;
        ConfigService = services.GetRequiredService<ILauncherConfigService>();
        InstallService = services.GetRequiredService<IKsp2InstallService>();
        FeedService = services.GetRequiredService<IReleasesFeedService>();
        InstallPlanService = services.GetRequiredService<IInstallPlanService>();
        _fileSystem = services.GetRequiredService<IFileSystem>();
        _feedProvider = services.GetRequiredService<IManifestReleasesFeedProviderService>();
        _log = services.GetRequiredService<ILogService>();
    }

    /// <summary>
    /// Gets the writer the verbs report through.
    /// </summary>
    public CliOutput Output { get; }

    /// <summary>
    /// Gets the launcher configuration service.
    /// </summary>
    public ILauncherConfigService ConfigService { get; }

    /// <summary>
    /// Gets the service holding the configured KSP2 installs.
    /// </summary>
    public IKsp2InstallService InstallService { get; }

    /// <summary>
    /// Gets the service holding the loaded release feeds, keyed by channel.
    /// </summary>
    public IReleasesFeedService FeedService { get; }

    /// <summary>
    /// Gets the service that describes and applies an install plan.
    /// </summary>
    public IInstallPlanService InstallPlanService { get; }

    /// <summary>
    /// The outcome of loading one feed out of the launcher config.
    /// </summary>
    /// <param name="Feed">The feed as configured in the launcher.</param>
    /// <param name="Channel">The channel the manifest declared, or null when the load failed.</param>
    /// <param name="Error">The reason the load failed, or null when it succeeded.</param>
    public sealed record FeedLoadResult(FeedInfo Feed, string? Channel, string? Error)
    {
        /// <summary>
        /// Gets a value indicating whether the feed loaded.
        /// </summary>
        public bool IsOk => Error is null;
    }

    /// <summary>
    /// Downloads every configured feed's manifest and registers it under the channel it declares.
    /// </summary>
    /// <returns>One result per configured feed, in config order.</returns>
    // A feed that fails is reported rather than thrown, so one dead feed does not stop a command
    // that only needed a different one.
    public async Task<IReadOnlyList<FeedLoadResult>> LoadFeedsAsync()
    {
        var cacheDirectory = _fileSystem.Path.Combine(ConfigService.GetLocalStorageDirectory(), DOWNLOAD_CACHE_FOLDER);
        _fileSystem.Directory.CreateDirectory(cacheDirectory);

        List<FeedLoadResult> results = [];
        foreach (var feed in ConfigService.Config.Feeds)
        {
            var manifestFeed = new ManifestReleasesFeed(_fileSystem, _feedProvider, _log, cacheDirectory, feed);
            try
            {
                if (!await manifestFeed.UpdateManifest())
                {
                    results.Add(new FeedLoadResult(feed, null, "manifest could not be downloaded or parsed"));
                    continue;
                }

                FeedService.AddOrSet(manifestFeed.CurrentChannel, manifestFeed);
                results.Add(new FeedLoadResult(feed, manifestFeed.CurrentChannel, null));
            }
            catch (Exception e)
            {
                results.Add(new FeedLoadResult(feed, null, e.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the install a command should act on, by id or by name, defaulting to the active install.
    /// </summary>
    /// <param name="selector">An install id or name, or null to use the active install.</param>
    /// <returns>The matching install entry, or null when nothing matched.</returns>
    public Ksp2InstallEntry? ResolveInstallEntry(string? selector)
    {
        InstallService.TryLoadKsp2Install();

        if (string.IsNullOrWhiteSpace(selector))
        {
            return InstallService.ActiveEntry;
        }

        if (Guid.TryParse(selector, out var id))
        {
            var entryById = InstallService.Entries.FirstOrDefault(e => e.Id == id);
            if (entryById is not null)
            {
                return entryById;
            }
        }

        var matches = InstallService.Entries
            .Where(e => string.Equals(e.Name, selector, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            Output.Error($"'{selector}' matches {matches.Count} installs by name. Use the id instead:");
            foreach (var match in matches)
            {
                Output.Error($"  {match.Id}  {match.Name}  {match.ExePath}");
            }
        }

        return null;
    }

    /// <summary>
    /// Reports that no install matched, listing what the launcher config holds.
    /// </summary>
    /// <param name="selector">The selector that failed to match, or null when the active install was used.</param>
    /// <returns>The install not found exit code.</returns>
    public int FailInstallNotFound(string? selector)
    {
        var message = string.IsNullOrWhiteSpace(selector)
            ? "No active KSP2 install is configured. Add one in the launcher, or name one explicitly."
            : $"No KSP2 install matched '{selector}'.";

        Output.Error(message);
        if (InstallService.Entries.Count == 0)
        {
            Output.Error("  (the launcher config lists no installs at all)");
            return ExitCode.INSTALL_NOT_FOUND;
        }

        Output.Error("Configured installs:");
        foreach (var entry in InstallService.Entries)
        {
            Output.Error($"  {entry.Id}  {entry.Name}  [{entry.ReleaseChannel}]  {entry.ExePath}");
        }

        return ExitCode.INSTALL_NOT_FOUND;
    }

    /// <summary>
    /// Reports that a channel has no feed, naming the channels that did load.
    /// </summary>
    /// <param name="channel">The channel that has no feed.</param>
    /// <param name="loaded">The results returned by <see cref="LoadFeedsAsync" />.</param>
    /// <returns>The feed not configured exit code.</returns>
    public int FailFeedNotConfigured(string channel, IReadOnlyList<FeedLoadResult> loaded)
    {
        Output.Error($"No feed for channel '{channel}' in the launcher config.");

        var available = loaded.Where(r => r.IsOk).Select(r => r.Channel!).ToList();
        if (available.Count > 0)
        {
            Output.Error($"Channels that loaded: {string.Join(", ", available)}");
        }

        foreach (var failure in loaded.Where(r => !r.IsOk))
        {
            Output.Error($"Feed {failure.Feed.Repository} / {failure.Feed.Filename} failed: {failure.Error}");
        }

        // The internal channel ships in no default config, so reaching here on a fresh launcher is
        // the expected outcome rather than a fault. Point at the setup, not just the symptom.
        if (string.Equals(channel, INTERNAL_CHANNEL, StringComparison.OrdinalIgnoreCase))
        {
            Output.Error("The internal channel is not configured by default. Add its feed to the");
            Output.Error("launcher settings using the internal testing instructions, then re-run.");
        }

        return ExitCode.FEED_NOT_CONFIGURED;
    }

    /// <summary>
    /// Picks the channel a command reads from, preferring an explicit choice over the install's own.
    /// </summary>
    /// <param name="explicitChannel">The channel named on the command line, or null.</param>
    /// <param name="entry">The install the command is acting on, or null.</param>
    /// <returns>The channel name, or null when neither source supplied one.</returns>
    // Defaulting to the install's channel rather than searching every feed keeps a command from
    // quietly repointing an install at a channel it was not configured for.
    public static string? ResolveChannel(string? explicitChannel, Ksp2InstallEntry? entry)
    {
        if (!string.IsNullOrWhiteSpace(explicitChannel))
        {
            return explicitChannel.Trim();
        }

        return string.IsNullOrWhiteSpace(entry?.ReleaseChannel) ? null : entry.ReleaseChannel;
    }

    /// <summary>
    /// Reports that a version selector matched nothing in a channel, naming where it does exist.
    /// </summary>
    /// <param name="selector">The version or build number that was requested.</param>
    /// <param name="channel">The channel that was searched.</param>
    /// <returns>The version not found exit code.</returns>
    public int FailVersionNotFound(string selector, string channel)
    {
        Output.Error($"Version '{selector}' is not published to channel '{channel}'.");

        foreach (var (otherChannel, feed) in FeedService.ReleasesFeed)
        {
            if (string.Equals(otherChannel, channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = FindVersion(feed.GetAllVersions(), selector);
            if (match is null)
            {
                continue;
            }

            Output.Error($"It exists in channel '{otherChannel}'. Name that channel explicitly if");
            Output.Error("this install is meant to switch channels.");
            break;
        }

        return ExitCode.VERSION_NOT_FOUND;
    }

    /// <summary>
    /// Matches a version selector against a channel's versions.
    /// </summary>
    /// <param name="versions">The versions to search.</param>
    /// <param name="selector">A full version such as 0.2.9.0.103669, or a bare build number such as 103669.</param>
    /// <returns>The matching version, or null when none matched.</returns>
    public static GameVersion? FindVersion(IEnumerable<GameVersion> versions, string selector)
    {
        var wanted = selector.Trim();
        return versions.FirstOrDefault(v =>
            string.Equals(FormatVersion(v), wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.BuildNumber, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renders a version the way the manifest and the game's own version string spell it.
    /// </summary>
    /// <param name="version">The version to render.</param>
    /// <returns>The version number and build number joined by a dot.</returns>
    public static string FormatVersion(GameVersion version) => $"{version.VersionNumber}.{version.BuildNumber}";
}
