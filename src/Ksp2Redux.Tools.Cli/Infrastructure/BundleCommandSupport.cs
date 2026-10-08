using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Setup shared by the stock bundle conversion commands.
/// </summary>
internal static class BundleCommandSupport
{
    // Bundle scanning is mostly waiting on the disk and the converted copies hold a bundle in
    // memory each, so more workers than this stop paying off and only cost memory.
    private const int MAX_WORKERS = 8;

    /// <summary>
    /// Checks the --game option and returns the folder it names.
    /// </summary>
    /// <param name="context">The command context.</param>
    /// <param name="settings">The parsed settings.</param>
    /// <param name="game">The full path of the game folder.</param>
    /// <param name="exitCode">The exit code to return when the check fails.</param>
    /// <returns>True when the folder exists.</returns>
    public static bool TryResolveGame(CliContext context, BundlesSettings settings, out string game, out int exitCode)
    {
        game = "";
        exitCode = ExitCode.SUCCESS;
        if (string.IsNullOrWhiteSpace(settings.Game))
        {
            exitCode = context.Output.Fail(ExitCode.USAGE_ERROR, "--game is required: the KSP2 folder to act on.");
            return false;
        }

        game = context.FileSystem.Path.GetFullPath(settings.Game.Trim().Trim('"'));
        if (!context.FileSystem.Directory.Exists(game))
        {
            exitCode = context.Output.Fail(ExitCode.PATH_NOT_FOUND, $"The game folder does not exist: {game}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs converter work behind a spinner on a terminal, or with progress lines on stderr otherwise.
    /// </summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="context">The command context.</param>
    /// <param name="game">The game folder.</param>
    /// <param name="label">The label shown while the work starts.</param>
    /// <param name="work">The work, given a converter that reports its progress.</param>
    /// <returns>Whatever the work returned.</returns>
    public static async Task<T> RunAsync<T>(CliContext context, string game, string label, Func<BundleConverter, T> work)
    {
        var workers = Math.Clamp(context.EnvironmentProvider.ProcessorCount, 1, MAX_WORKERS);
        if (!context.Output.Capabilities.CanAnimate)
        {
            context.Output.Progress(label);
            var options = new BundleConversionOptions { MaxParallelism = workers, Progress = new LineProgress(context.Output.Progress) };
            return await Task.Run(() => work(new BundleConverter(context.FileSystem, game, options)));
        }

        return await context.Output.StatusAsync(label, update =>
        {
            var options = new BundleConversionOptions { MaxParallelism = workers, Progress = new LineProgress(update) };
            return Task.Run(() => work(new BundleConverter(context.FileSystem, game, options)));
        });
    }

    /// <summary>
    /// Forwards progress lines from the converter's worker threads one at a time.
    /// </summary>
    private sealed class LineProgress : IProgress<string>
    {
        private readonly Action<string> _write;
        private readonly object _gate = new();

        public LineProgress(Action<string> write)
        {
            _write = write;
        }

        public void Report(string value)
        {
            lock (_gate)
            {
                _write(value);
            }
        }
    }
}
