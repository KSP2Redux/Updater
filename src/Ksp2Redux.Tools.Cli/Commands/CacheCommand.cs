using System.IO.Abstractions;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Reports what the download cache holds.
/// </summary>
public sealed class CacheCommand : ReduxCommand<CacheSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        CacheSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = context.DownloadCacheDirectory;
        var files = CacheFiles.In(context, directory);
        var bytes = files.Sum(file => file.Length);

        context.Output.Payload(
            new
            {
                ok = true,
                directory,
                files = files.Count,
                bytes,
            },
            () =>
            {
                context.Output.Result(directory);
                context.Output.Detail($"  {files.Count} files, {CliFormat.Bytes(bytes)}");
                WriteBreakdown(context, files);
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }

    // Grouping by extension answers the question the command is asked, which is what is taking up
    // the space: downloaded patches or the manifests they were resolved from.
    private static void WriteBreakdown(CliContext context, IReadOnlyList<IFileInfo> files)
    {
        if (!context.Output.Capabilities.FancyResults || files.Count == 0)
        {
            return;
        }

        var groups = files
            .GroupBy(file => string.IsNullOrEmpty(file.Extension) ? "(no extension)" : file.Extension.ToLowerInvariant())
            .Select(group => (Kind: group.Key, Megabytes: group.Sum(file => file.Length) / (1024d * 1024)))
            .Where(group => group.Megabytes > 0)
            .OrderByDescending(group => group.Megabytes)
            .ToList();

        if (groups.Count == 0)
        {
            return;
        }

        Color[] palette = [CliTheme.BRAND_ORANGE, CliTheme.SUCCESS, CliTheme.WARNING, CliTheme.SECONDARY, CliTheme.BRAND_RED];

        BreakdownChart chart = new();
        for (var index = 0; index < groups.Count; index++)
        {
            chart.AddItem(groups[index].Kind, Math.Round(groups[index].Megabytes, 1), palette[index % palette.Length]);
        }

        context.Output.ResultConsole.Write(chart);
    }
}
