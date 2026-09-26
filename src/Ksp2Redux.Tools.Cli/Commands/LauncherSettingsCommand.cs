using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Shows the launcher-wide settings: patch source, concurrent chunks and verbose logging.
/// </summary>
public sealed class LauncherSettingsCommand : ReduxCommand<LauncherSettingsSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        LauncherSettingsSettings settings,
        CancellationToken cancellationToken)
    {
        Report(context);
        return Task.FromResult(ExitCode.SUCCESS);
    }

    /// <summary>
    /// Writes the current launcher-wide settings, as text or as a JSON document.
    /// </summary>
    /// <param name="context">The context whose config is reported.</param>
    internal static void Report(CliContext context)
    {
        var config = context.ConfigService.Config;
        context.Output.Payload(
            new
            {
                ok = true,
                patchSource = config.PatchDownloadSource.ToString().ToLowerInvariant(),
                concurrentChunks = config.MaxConcurrentChunkDownloads,
                verboseLogging = config.VerboseLogging,
                configPath = config.StoragePath,
            },
            () =>
            {
                context.Output.Result($"  {"patch source:",-20}{config.PatchDownloadSource.ToString().ToLowerInvariant()}");
                context.Output.Result($"  {"concurrent chunks:",-20}{config.MaxConcurrentChunkDownloads}");
                context.Output.Result($"  {"verbose logging:",-20}{(config.VerboseLogging ? "on" : "off")}");
            });
    }
}
