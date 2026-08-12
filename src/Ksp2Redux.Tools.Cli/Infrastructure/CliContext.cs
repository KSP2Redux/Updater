using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The setup every command shares: the launcher config, the KSP2 install to act on, and the release feeds.
/// </summary>
public sealed class CliContext
{
    /// <summary>
    /// How much of an install id the CLI shows, and the least it accepts back as a selector.
    /// </summary>
    public const int ID_PREFIX_LENGTH = 8;

    private const string DOWNLOAD_CACHE_FOLDER = "download-cache";
    private const string INTERNAL_CHANNEL = "internal";

    private readonly IFileSystem _fileSystem;
    private readonly IModuleDefinitionService _moduleDefinitions;
    private readonly IManifestReleasesFeedProviderService _feedProvider;
    private readonly ILogService _log;

    /// <summary>
    /// Initializes the context by resolving the launcher services the commands need.
    /// </summary>
    /// <param name="services">The container built by <see cref="CliServiceProvider" />.</param>
    /// <param name="output">The writer the commands report through.</param>
    public CliContext(IServiceProvider services, CliOutput output)
    {
        Output = output;
        ConfigService = services.GetRequiredService<ILauncherConfigService>();
        InstallService = services.GetRequiredService<IKsp2InstallService>();
        FeedService = services.GetRequiredService<IReleasesFeedService>();
        InstallPlanService = services.GetRequiredService<IInstallPlanService>();
        DetectorService = services.GetRequiredService<IKsp2DetectorService>();
        OperatingSystemService = services.GetRequiredService<IOperatingSystemService>();
        EnvironmentProvider = services.GetRequiredService<IEnvironmentProvider>();
        EnvironmentVariables = services.GetRequiredService<IEnvironmentVariableProvider>();
        AssemblyService = services.GetRequiredService<IAssemblyService>();
        DiskSpaceService = services.GetRequiredService<IDiskSpaceService>();
        FileSystem = services.GetRequiredService<IFileSystem>();
        _fileSystem = FileSystem;
        _moduleDefinitions = services.GetRequiredService<IModuleDefinitionService>();
        _feedProvider = services.GetRequiredService<IManifestReleasesFeedProviderService>();
        _log = services.GetRequiredService<ILogService>();
    }

    /// <summary>
    /// Gets the service that scans the well known Steam and Epic locations for KSP2.
    /// </summary>
    public IKsp2DetectorService DetectorService { get; }

    /// <summary>
    /// Gets the service answering which platform this is.
    /// </summary>
    public IOperatingSystemService OperatingSystemService { get; }

    /// <summary>
    /// Gets the process's own environment, which knows where its executable lives.
    /// </summary>
    public IEnvironmentProvider EnvironmentProvider { get; }

    /// <summary>
    /// Gets the reader and writer for environment variables, including the user's PATH.
    /// </summary>
    public IEnvironmentVariableProvider EnvironmentVariables { get; }

    /// <summary>
    /// Gets the running assembly, which carries the version the CLI reports.
    /// </summary>
    public IAssemblyService AssemblyService { get; }

    /// <summary>
    /// Gets the version this build reports, or 0.0.0.0 when it has none.
    /// </summary>
    public Version RunningVersion => AssemblyService.GetVersion() ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Builds a service that reads the CLI's own releases off GitHub.
    /// </summary>
    /// <param name="timeout">How long to wait on GitHub, or null for the default.</param>
    /// <returns>A release service pointed at the configured repository.</returns>
    public CliReleaseService CreateReleaseService(TimeSpan? timeout = null) => new(
        ConfigService.Config.LauncherRepo,
        OperatingSystemService.IsLinux(),
        RunningVersion.ToString(),
        timeout);

    /// <summary>
    /// Gets the service reporting free space on the drive holding an install.
    /// </summary>
    public IDiskSpaceService DiskSpaceService { get; }

    /// <summary>
    /// Gets the file system the launcher services read and write through.
    /// </summary>
    public IFileSystem FileSystem { get; }

    /// <summary>
    /// Gets the launcher's log service, which knows the file this session is writing to.
    /// </summary>
    public ILogService LogService => _log;

    /// <summary>
    /// Gets the folder holding the manifests downloaded from the configured feeds.
    /// </summary>
    public string DownloadCacheDirectory =>
        _fileSystem.Path.Combine(ConfigService.GetLocalStorageDirectory(), DOWNLOAD_CACHE_FOLDER);

    /// <summary>
    /// Gets the writer the commands report through.
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
    public Task<IReadOnlyList<FeedLoadResult>> LoadFeedsAsync()
    {
        return Output.StatusAsync<IReadOnlyList<FeedLoadResult>>("Loading release feeds", async update =>
        {
            var cacheDirectory = _fileSystem.Path.Combine(ConfigService.GetLocalStorageDirectory(), DOWNLOAD_CACHE_FOLDER);
            _fileSystem.Directory.CreateDirectory(cacheDirectory);

            List<FeedLoadResult> results = [];
            foreach (var feed in ConfigService.Config.Feeds)
            {
                update($"Loading {feed.Filename}");
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
        });
    }

    /// <summary>
    /// Finds the installation a command should act on, by id or by name, defaulting to the active installation.
    /// </summary>
    /// <param name="selector">An installation id or name, or null to use the active installation.</param>
    /// <returns>The matching installation entry, or null when nothing matched.</returns>
    public Ksp2InstallEntry? ResolveInstallEntry(string? selector)
    {
        InstallService.TryLoadKsp2Install();

        if (string.IsNullOrWhiteSpace(selector))
        {
            if (InstallService.ActiveEntry is { } active)
            {
                return active;
            }

            return Output.Capabilities.CanPrompt && InstallService.Entries.Count > 0
                ? Prompt("No active KSP2 install is configured. Pick one:", InstallService.Entries)
                : null;
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

        // The installs table shows a shortened id on a terminal, so the shortened form has to work
        // when it is handed back in.
        if (matches.Count == 0 && selector.Length >= ID_PREFIX_LENGTH)
        {
            matches = InstallService.Entries
                .Where(e => e.Id.ToString().StartsWith(selector, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            if (Output.Capabilities.CanPrompt)
            {
                return Prompt($"'{selector}' matches {matches.Count} installs by name. Pick one:", matches);
            }

            Output.Error($"'{selector}' matches {matches.Count} installs by name. Use the id instead:");
            foreach (var match in matches)
            {
                Output.Error($"  {match.Id}  {match.Name}  {match.ExePath}");
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the launcher config back off disk and checks the change actually landed.
    /// </summary>
    /// <param name="expectation">The check to run against the config as it now exists on disk.</param>
    /// <returns>True when the file could be read and satisfies <paramref name="expectation" />.</returns>
    // LauncherConfigService.Save swallows a write failure and reports it through a dialog, which in
    // a window less process is only a warning. Without this check a command that changed nothing on
    // disk would still report success.
    public bool ConfigPersisted(Func<LauncherConfig, bool> expectation)
    {
        try
        {
            var path = ConfigService.Config.StoragePath;
            if (string.IsNullOrWhiteSpace(path) || !_fileSystem.File.Exists(path))
            {
                return false;
            }

            var saved = JsonSerializer.Deserialize<LauncherConfig>(_fileSystem.File.ReadAllText(path));
            return saved is not null && expectation(saved);
        }
        catch (Exception e)
        {
            _log.Error("Could not read the launcher config back after saving it", e);
            return false;
        }
    }

    /// <summary>
    /// Turns a path to a KSP2 folder or executable into the executable path the launcher stores.
    /// </summary>
    /// <param name="path">A path to the KSP2 folder or to KSP2_x64.exe.</param>
    /// <returns>The path to the executable, which may not exist.</returns>
    public string ResolveExePath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return _fileSystem.Directory.Exists(trimmed)
            ? _fileSystem.Path.Combine(trimmed, Ksp2Install.KSP2_EXE_NAME)
            : trimmed;
    }

    /// <summary>
    /// Reads a KSP2 install off disk without touching the launcher config.
    /// </summary>
    /// <param name="exePath">The path to KSP2_x64.exe.</param>
    /// <returns>The install, or null when it could not be read at all.</returns>
    public Ksp2Install? ReadInstall(string exePath)
    {
        try
        {
            return new Ksp2Install(_fileSystem, _moduleDefinitions, exePath);
        }
        catch (Exception e)
        {
            _log.Error($"Could not read a KSP2 install at {exePath}", e);
            return null;
        }
    }

    /// <summary>
    /// Asks the user to pick an installation out of a list.
    /// </summary>
    /// <param name="title">The question shown above the list.</param>
    /// <param name="entries">The installations to choose from.</param>
    /// <returns>The installation the user picked.</returns>
    private Ksp2InstallEntry Prompt(string title, IReadOnlyList<Ksp2InstallEntry> entries)
    {
        SelectionPrompt<Ksp2InstallEntry> prompt = new()
        {
            Title = $"[{CliTheme.HEADER_STYLE}]{Markup.Escape(title)}[/]",
            HighlightStyle = new Style(CliTheme.BRAND_ORANGE),
        };

        prompt.UseConverter(entry =>
            $"{Markup.Escape(entry.Name)} [{CliTheme.DETAIL_STYLE}]{Markup.Escape(entry.ReleaseChannel)}  {Markup.Escape(entry.ExePath)}[/]");
        prompt.AddChoices(entries);

        return Output.ProgressConsole.Prompt(prompt);
    }

    /// <summary>
    /// Reports that no install matched, listing what the launcher config holds.
    /// </summary>
    /// <param name="selector">The selector that failed to match or null when the active installation was used.</param>
    /// <returns>The install didn't found exit code.</returns>
    public int FailInstallNotFound(string? selector)
    {
        var message = string.IsNullOrWhiteSpace(selector)
            ? "No active KSP2 install is configured. Add one in the launcher, or name one explicitly."
            : $"No KSP2 install matched '{selector}'.";

        Output.Error(message);
        if (InstallService.Entries.Count == 0)
        {
            Output.ErrorDetail("  (the launcher config lists no installs at all)");
            return ExitCode.INSTALL_NOT_FOUND;
        }

        Output.ErrorDetail("Configured installs:");
        foreach (var entry in InstallService.Entries)
        {
            Output.ErrorDetail($"  {entry.Id}  {entry.Name}  [{entry.ReleaseChannel}]  {entry.ExePath}");
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
            Output.ErrorDetail($"Channels that loaded: {string.Join(", ", available)}");
        }

        foreach (var failure in loaded.Where(r => !r.IsOk))
        {
            Output.Warn($"Feed {failure.Feed.Repository} / {failure.Feed.Filename} failed: {failure.Error}");
        }

        // The internal channel ships in no default config, so reaching here on a fresh launcher is
        // the expected outcome rather than a fault. Point at the setup, not just the symptom.
        if (string.Equals(channel, INTERNAL_CHANNEL, StringComparison.OrdinalIgnoreCase))
        {
            Output.ErrorDetail("The internal channel is not configured by default. Add its feed to the");
            Output.ErrorDetail("launcher settings using the internal testing instructions, then re-run.");
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

            Output.ErrorDetail($"It exists in channel '{otherChannel}'. Name that channel explicitly if");
            Output.ErrorDetail("this install is meant to switch channels.");
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
