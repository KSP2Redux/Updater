using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Prints the launcher's most recent log file.
/// </summary>
public sealed class LogsCommand : ReduxCommand<LogsSettings>
{
    private const string LOG_PATTERN = "launcher-*.log";
    private const int POLL_MILLISECONDS = 500;

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        LogsSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.ShouldFollow && settings.IsJson)
        {
            return context.Output.Fail(ExitCode.USAGE_ERROR, "--follow cannot be combined with --json.");
        }

        var log = MostRecentLog(context);
        if (log is null)
        {
            return context.Output.Fail(ExitCode.USAGE_ERROR, "The launcher has not written any log files yet.");
        }

        if (settings.PathOnly)
        {
            context.Output.Payload(new { ok = true, path = log.FullName }, () => context.Output.Result(log.FullName));
            return ExitCode.SUCCESS;
        }

        var lines = ReadLines(context, log);
        var tail = settings.Tail > 0 && lines.Count > settings.Tail
            ? [.. lines.Skip(lines.Count - settings.Tail)]
            : lines;

        context.Output.Heading(log.FullName);
        context.Output.Payload(
            new { ok = true, path = log.FullName, lines = tail },
            () =>
            {
                foreach (var line in tail)
                {
                    context.Output.Result(line);
                }
            });

        if (!settings.ShouldFollow)
        {
            return ExitCode.SUCCESS;
        }

        await FollowAsync(context, log, cancellationToken);
        return ExitCode.SUCCESS;
    }

    // The command's own run created the newest log file from the LogService constructor, so the
    // interesting one is the newest file that is not this session's.
    private static IFileInfo? MostRecentLog(CliContext context)
    {
        var current = context.LogService.CurrentLogFilePath;
        var directory = current is null
            ? context.ConfigService.GetLocalStorageDirectory()
            : context.FileSystem.Path.GetDirectoryName(current);

        if (string.IsNullOrWhiteSpace(directory) || !context.FileSystem.Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return context.FileSystem.Directory
                .EnumerateFiles(directory, LOG_PATTERN)
                .Where(path => !string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
                .Select(context.FileSystem.FileInfo.New)
                .MaxBy(file => file.LastWriteTimeUtc);
        }
        catch (Exception e)
        {
            context.LogService.Error($"Could not list the log files in {directory}", e);
            return null;
        }
    }

    // The launcher may be running and holding the file open, so everything here reads with the
    // widest share it can ask for.
    private static List<string> ReadLines(CliContext context, IFileInfo log)
    {
        try
        {
            using var stream = context.FileSystem.FileStream.New(
                log.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using StreamReader reader = new(stream, Encoding.UTF8);

            List<string> lines = [];
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception e)
        {
            context.LogService.Error($"Could not read {log.FullName}", e);
            return [];
        }
    }

    private static async Task FollowAsync(CliContext context, IFileInfo log, CancellationToken cancellationToken)
    {
        var position = log.Exists ? log.Length : 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(POLL_MILLISECONDS, cancellationToken);

                var current = context.FileSystem.FileInfo.New(log.FullName);
                if (!current.Exists || current.Length <= position)
                {
                    position = current.Exists ? Math.Min(position, current.Length) : 0;
                    continue;
                }

                using var stream = context.FileSystem.FileStream.New(
                    log.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                stream.Seek(position, SeekOrigin.Begin);
                using StreamReader reader = new(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    context.Output.Result(line);
                }

                position = stream.Position;
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is how this command is meant to end.
        }
    }
}
