using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Changes the launcher-wide settings, which the launcher window and the CLI share.
/// </summary>
public sealed class LauncherSettingsSetCommand : ReduxCommand<LauncherSettingsSetSettings>
{
    private const int MINIMUM_CHUNKS = 1;
    private const int MAXIMUM_CHUNKS = 8;

    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        LauncherSettingsSetSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings is { PatchSource: null, ConcurrentChunks: null, VerboseLogging: null })
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "Nothing to change. Pass at least one of --patch-source, --concurrent-chunks or --verbose-logging."));
        }

        PatchDownloadSource? source = null;
        if (settings.PatchSource is not null)
        {
            if (!Enum.TryParse<PatchDownloadSource>(settings.PatchSource.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "--patch-source takes r2 or github."));
            }

            source = parsed;
        }

        if (settings.ConcurrentChunks is < MINIMUM_CHUNKS or > MAXIMUM_CHUNKS)
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.USAGE_ERROR,
                $"--concurrent-chunks takes a number from {MINIMUM_CHUNKS} to {MAXIMUM_CHUNKS}."));
        }

        if (!CliSwitch.TryParse(settings.VerboseLogging, out var verbose))
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "--verbose-logging takes on or off."));
        }

        var config = context.ConfigService.Config;
        if (source is { } newSource) config.PatchDownloadSource = newSource;
        if (settings.ConcurrentChunks is { } chunks) config.MaxConcurrentChunkDownloads = chunks;
        if (verbose is { } newVerbose) config.VerboseLogging = newVerbose;
        context.ConfigService.Save();

        if (!context.ConfigPersisted(saved =>
                (source is null || saved.PatchDownloadSource == source)
                && (settings.ConcurrentChunks is null || saved.MaxConcurrentChunkDownloads == settings.ConcurrentChunks)
                && (verbose is null || saved.VerboseLogging == verbose)))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"The settings could not be written to the launcher config at {config.StoragePath}."));
        }

        LauncherSettingsCommand.Report(context);
        return Task.FromResult(ExitCode.SUCCESS);
    }
}
