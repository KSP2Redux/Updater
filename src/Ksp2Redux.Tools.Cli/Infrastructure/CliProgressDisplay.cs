using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Reports the progress of an install plan, as a live bar on a terminal and as text otherwise.
/// </summary>
public static class CliProgressDisplay
{
    private const long PROGRESS_STEP_BYTES = 32L * 1024 * 1024;
    private const double BYTES_PER_MB = 1024d * 1024d;

    /// <summary>
    /// Runs an install plan, wiring its three callbacks to whichever report the stream supports.
    /// </summary>
    /// <param name="output">The writer the report is drawn through.</param>
    /// <param name="apply">
    /// The work to run, given a log callback, a downloaded and total bytes callback, and a current
    /// and total step callback.
    /// </param>
    /// <returns>A task that completes when <paramref name="apply" /> does.</returns>
    public static Task RunAsync(
        CliOutput output,
        Func<Action<string>, Action<long, long>, Action<int, int>, Task> apply)
    {
        return output.Capabilities.CanAnimate
            ? RunAnimatedAsync(output, apply)
            : RunPlainAsync(output, apply);
    }

    // Byte level progress arrives every 100ms from the downloader, which is far too chatty for a
    // log file. Report on crossing a size boundary instead, plus the final byte.
    private static Task RunPlainAsync(
        CliOutput output,
        Func<Action<string>, Action<long, long>, Action<int, int>, Task> apply)
    {
        var lastReportedBytes = 0L;

        return apply(
            output.Progress,
            (current, total) =>
            {
                if (current < total && current - lastReportedBytes < PROGRESS_STEP_BYTES)
                {
                    return;
                }

                lastReportedBytes = current;
                output.Progress($"  downloaded {current / BYTES_PER_MB:F0} of {total / BYTES_PER_MB:F0} MB");
            },
            (current, total) =>
            {
                lastReportedBytes = 0;
                output.Progress($"step {current} of {total}");
            });
    }

    private static Task RunAnimatedAsync(
        CliOutput output,
        Func<Action<string>, Action<long, long>, Action<int, int>, Task> apply)
    {
        return output.ProgressConsole
            .Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new SpinnerColumn(Spinner.Known.Dots) { Style = new Style(CliTheme.BRAND_ORANGE) },
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn { FinishedStyle = new Style(CliTheme.SUCCESS) },
                new WhileDownloadingColumn(new PercentageColumn()),
                new WhileDownloadingColumn(new DownloadedColumn()),
                new WhileDownloadingColumn(new TransferSpeedColumn()))
            .StartAsync(async progress =>
            {
                var task = progress.AddTask("Starting", new ProgressTaskSettings { AutoStart = true });
                task.IsIndeterminate = true;

                var step = "";

                await apply(
                    line => task.Description = Markup.Escape(step.Length == 0 ? line : $"{step}  {line}"),
                    (current, total) =>
                    {
                        task.State.Update<bool>(WhileDownloadingColumn.STATE_KEY, _ => true);
                        task.IsIndeterminate = false;
                        task.MaxValue = total;
                        task.Value = current;
                    },
                    (current, total) =>
                    {
                        step = $"step {current} of {total}";
                        task.Description = step;
                        task.State.Update<bool>(WhileDownloadingColumn.STATE_KEY, _ => false);
                        task.IsIndeterminate = true;
                        task.MaxValue = 1;
                        task.Value = 0;
                    });

                task.State.Update<bool>(WhileDownloadingColumn.STATE_KEY, _ => false);
                task.IsIndeterminate = false;
                task.MaxValue = 1;
                task.Value = 1;
                task.StopTask();
            });
    }

    /// <summary>
    /// Draws an inner column only while the task is tracking a download.
    /// </summary>
    private sealed class WhileDownloadingColumn : ProgressColumn
    {
        /// <summary>
        /// The task state key holding whether the task is tracking a download.
        /// </summary>
        public const string STATE_KEY = "isDownload";

        private readonly ProgressColumn _inner;

        public WhileDownloadingColumn(ProgressColumn inner)
        {
            _inner = inner;
        }

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            return task.State.Get<bool>(STATE_KEY)
                ? _inner.Render(options, task, deltaTime)
                : new Text(string.Empty);
        }
    }
}
