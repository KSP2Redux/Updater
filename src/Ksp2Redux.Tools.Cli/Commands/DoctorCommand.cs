using System.Reflection;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Reports the paths, installs and feeds the launcher can see, for pasting into a support thread.
/// </summary>
public sealed class DoctorCommand : ReduxCommand<DoctorSettings>
{
    private sealed record InstallReport(
        Guid Id,
        string Name,
        bool Active,
        string Channel,
        string ExePath,
        bool Valid,
        string? Version,
        string? Distribution,
        long? FreeSpaceBytes);

    private const int LABEL_WIDTH = 14;



    private sealed record FeedReport(string? Channel, string Repository, string Filename, bool Ok, string? Error);

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        DoctorSettings settings,
        CancellationToken cancellationToken)
    {
        context.InstallService.TryLoadKsp2Install();
        var activeId = context.InstallService.ActiveEntry?.Id;

        var installs = context.InstallService.Entries
            .Select(entry =>
            {
                var install = context.ReadInstall(entry.ExePath);
                return new InstallReport(
                    entry.Id,
                    entry.Name,
                    entry.Id == activeId,
                    entry.ReleaseChannel,
                    entry.ExePath,
                    install is { IsValid: true },
                    install?.GameVersion is null ? null : CliContext.FormatVersion(install.GameVersion),
                    install?.Distribution.ToString(),
                    context.DiskSpaceService.GetAvailableFreeSpace(entry.ExePath));
            })
            .ToList();

        List<FeedReport> feeds = [];
        if (!settings.IsOffline)
        {
            feeds.AddRange((await context.LoadFeedsAsync())
                .Select(r => new FeedReport(r.Channel, r.Feed.Repository, r.Feed.Filename, r.IsOk, r.Error)));
        }

        var detected = context.DetectorService.DetectKsp2InstallLocation();
        var cacheDirectory = context.DownloadCacheDirectory;
        var cacheBytes = DirectorySize(context, cacheDirectory);

        context.Output.Payload(
            new
            {
                ok = true,
                version = Version(),
                configPath = context.ConfigService.Config.StoragePath,
                storageDirectory = context.ConfigService.GetLocalStorageDirectory(),
                logFile = context.LogService.CurrentLogFilePath,
                downloadCache = cacheDirectory,
                downloadCacheBytes = cacheBytes,
                detectedInstall = detected,
                gameDataFolder = context.GameDataFolderService.Resolve(context.InstallService.ActiveEntry),
                installs,
                feeds,
                feedsChecked = !settings.IsOffline,
            },
            () => WriteReport(context, settings, installs, feeds, detected, cacheDirectory, cacheBytes));

        return ExitCode.SUCCESS;
    }

    private static void WriteReport(
        CliContext context,
        DoctorSettings settings,
        IReadOnlyList<InstallReport> installs,
        IReadOnlyList<FeedReport> feeds,
        string? detected,
        string cacheDirectory,
        long cacheBytes)
    {
        var fancy = context.Output.Capabilities.FancyResults;

        context.Output.Result($"redux-launcher-cli {Version()}");

        context.Output.Section("Paths");
        WritePath(context, "config", context.ConfigService.Config.StoragePath);
        WritePath(context, "storage", context.ConfigService.GetLocalStorageDirectory());
        WritePath(context, "log file", context.LogService.CurrentLogFilePath);
        WritePath(context, "download cache", $"{cacheDirectory} ({CliFormat.Bytes(cacheBytes)})");
        WritePath(context, "detected KSP2", detected);
        WritePath(context, "game data", context.GameDataFolderService.Resolve(context.InstallService.ActiveEntry));

        context.Output.Section("Installs");
        if (installs.Count == 0)
        {
            context.Output.Result("  (the launcher config lists none)");
        }

        foreach (var install in installs)
        {
            context.Output.Result($"{(install.Active ? "*" : " ")} {install.Name}  [{install.Channel}]  {install.Id}");
            WritePath(context, "  path", install.ExePath);
            context.Output.Result($"    {"state:",-14}{(install.Valid ? install.Distribution : "not a valid KSP2 install")}");
            context.Output.Result($"    {"version:",-14}{install.Version ?? "(unknown)"}");
            if (!fancy)
            {
                context.Output.Result($"    {"free:",-14}{(install.FreeSpaceBytes is { } free ? CliFormat.Bytes(free) : "(unknown)")}");
            }
        }

        WriteFreeSpaceChart(context, installs);

        context.Output.Section("Feeds");
        if (settings.IsOffline)
        {
            context.Output.Result("  (skipped, --offline)");
            return;
        }

        if (feeds.Count == 0)
        {
            context.Output.Result("  (the launcher config lists none)");
        }

        foreach (var feed in feeds)
        {
            context.Output.Result(feed.Ok
                ? $"  ok      {feed.Channel}  {feed.Repository} / {feed.Filename}"
                : $"  FAILED  {feed.Repository} / {feed.Filename}: {feed.Error}");
        }
    }

    // A path is the one value here long enough to wrap, so on a terminal it is drawn as a path that
    // shortens from the middle instead.
    private static void WritePath(CliContext context, string label, string? path)
    {
        var caption = $"{label}:";

        if (string.IsNullOrWhiteSpace(path))
        {
            context.Output.Result($"  {caption,-16}(none)");
            return;
        }

        if (!context.Output.Capabilities.FancyResults)
        {
            context.Output.Result($"  {caption,-16}{path}");
            return;
        }

        Grid grid = new();
        grid.AddColumn(new GridColumn().NoWrap().Width(LABEL_WIDTH + 4));
        grid.AddColumn();
        grid.AddRow(new Markup($"  [{CliTheme.DETAIL_STYLE}]{Markup.Escape(caption)}[/]"), CliOutput.Render(CliCell.Path(path)));
        context.Output.ResultConsole.Write(grid);
    }

    private static void WriteFreeSpaceChart(CliContext context, IReadOnlyList<InstallReport> installs)
    {
        var measured = installs.Where(install => install.FreeSpaceBytes is > 0).ToList();
        if (!context.Output.Capabilities.FancyResults || measured.Count == 0)
        {
            return;
        }

        BarChart chart = new()
        {
            Label = $"[{CliTheme.DETAIL_STYLE}]free disk space, GB[/]",
            LabelAlignment = Justify.Left,
            ShowValues = true,
        };

        foreach (var install in measured)
        {
            chart.AddItem(install.Name, Math.Round(install.FreeSpaceBytes!.Value / (1024d * 1024 * 1024), 1), CliTheme.SUCCESS);
        }

        context.Output.ResultConsole.Write(chart);
    }

    private static string Version() =>
        typeof(DoctorCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static long DirectorySize(CliContext context, string directory)
    {
        try
        {
            if (!context.FileSystem.Directory.Exists(directory))
            {
                return 0;
            }

            return context.FileSystem.Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => context.FileSystem.FileInfo.New(file).Length);
        }
        catch (Exception e)
        {
            context.LogService.Error($"Could not measure the download cache at {directory}", e);
            return 0;
        }
    }
}
