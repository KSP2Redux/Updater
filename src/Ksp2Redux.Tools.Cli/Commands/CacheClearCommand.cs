using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Deletes the manifests and patch downloads the launcher keeps between runs.
/// </summary>
public sealed class CacheClearCommand : ReduxCommand<CacheClearSettings>
{
    /// <inheritdoc />
    // Only the download cache under the launcher's own storage folder is touched. The stock file
    // cache inside a KSP2 install is what uninstall restores from, and deleting that would strand
    // the install on a patched state it cannot revert.
    protected override Task<int> RunAsync(
        CliContext context,
        CacheClearSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.OlderThanDays is < 0)
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "--older-than cannot be negative."));
        }

        var directory = context.DownloadCacheDirectory;
        var candidates = CacheFiles.In(context, directory);

        if (settings.OlderThanDays is { } days)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            candidates = [.. candidates.Where(file => file.LastWriteTimeUtc < cutoff)];
        }

        if (candidates.Count == 0)
        {
            context.Output.Payload(
                new { ok = true, directory, deleted = 0, bytes = 0L },
                () => context.Output.Result("0"));

            return Task.FromResult(ExitCode.SUCCESS);
        }

        var bytes = candidates.Sum(file => file.Length);
        var answer = CliConfirm.Ask(
            context.Output,
            settings.AssumeYes,
            $"Delete {candidates.Count} cached files ({CliFormat.Bytes(bytes)}) from {directory}?",
            requireAnswer: true);

        switch (answer)
        {
            case ConfirmAnswer.Declined:
                return Task.FromResult(context.Output.Fail(ExitCode.CANCELLED, "Nothing was deleted."));
            case ConfirmAnswer.NeedsFlag:
                return Task.FromResult(context.Output.Fail(
                    ExitCode.USAGE_ERROR,
                    "Refusing to delete without a terminal to confirm on. Pass --yes."));
            case ConfirmAnswer.Approved:
            default:
                break;
        }

        var deleted = 0;
        var deletedBytes = 0L;
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = file.Length;
                file.Delete();
                deleted++;
                deletedBytes += length;
            }
            catch (Exception e)
            {
                context.Output.Warn($"Could not delete {file.FullName}: {e.Message}");
            }
        }

        context.Output.Payload(
            new { ok = true, directory, deleted, bytes = deletedBytes },
            () =>
            {
                context.Output.Result(deleted.ToString());
                context.Output.Detail($"  freed {CliFormat.Bytes(deletedBytes)} from {directory}");
            });

        return Task.FromResult(deleted == candidates.Count ? ExitCode.SUCCESS : ExitCode.USAGE_ERROR);
    }
}
