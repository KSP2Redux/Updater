using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Base for every command, holding the setup they all share.
/// </summary>
/// <typeparam name="TSettings">The settings the command is parsed into.</typeparam>
public abstract class ReduxCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : BaseSettings
{
    /// <inheritdoc />
    // The launcher's LogService writes its banner and every log line to Console.Out, which would
    // corrupt a JSON document and interleave with results in text mode. Capture the real stdout for
    // results, then point Console.Out at stderr so any launcher write lands there instead.
    protected sealed override async Task<int> ExecuteAsync(
        CommandContext context,
        TSettings settings,
        CancellationToken cancellationToken)
    {
        var results = Console.Out;
        Console.SetOut(Console.Error);

        var capabilities = CliCapabilities.Detect(settings.Color, settings.IsJson, settings.IsVerbose, settings.IsQuiet);
        var output = new CliOutput(results, settings.IsJson, capabilities);
        try
        {
            var services = CliServiceProvider.Build(output);

            // LogService writes a session header to the console from its constructor, bypassing the
            // level gate below. That header belongs in the log file, so swallow the console copy by
            // muting Console.Out across the one resolve that constructs it.
            if (!settings.IsVerbose)
            {
                Console.SetOut(TextWriter.Null);
            }

            var log = services.GetRequiredService<ILogService>();
            Console.SetOut(Console.Error);

            // The launcher logs at Info, which is right for a session that runs for hours behind a
            // window and far too chatty for a command that runs for seconds. Warn and above still
            // reaches stderr, and the full log still reaches the log file either way.
            log.MinimumLevel = settings.IsVerbose ? LogLevel.Debug : LogLevel.Warn;

            CliContext cliContext = new(services, output);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CliCancellation.Token);

            var exitCode = await RunAsync(cliContext, settings, cancellation.Token);

            // After the command rather than before it, so the notice sits under the output the user
            // actually asked for instead of pushing it down the screen.
            if (NoticeApplies)
            {
                await CliUpdateNotice.NotifyAsync(cliContext, !settings.NoUpdateCheck, cancellation.Token);
            }

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            return output.Fail(ExitCode.CANCELLED, "cancelled.");
        }
        catch (Exception e)
        {
            // The message alone is enough to act on for the failures a user can cause. A stack is
            // only worth the noise when someone has already asked for the detail.
            if (settings.IsVerbose && capabilities.FancyProgress)
            {
                output.ProgressConsole.WriteException(e, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes);
            }

            return output.Fail(ExitCode.USAGE_ERROR, e.Message);
        }
    }

    /// <summary>
    /// Gets a value indicating whether this command tells the user about a newer CLI when it finishes.
    /// </summary>
    // The commands that are about the CLI's own version do their own reporting, and a second notice
    // underneath would only repeat them.
    protected virtual bool NoticeApplies => true;

    /// <summary>
    /// Runs the command against a prepared context.
    /// </summary>
    /// <param name="context">The setup every command shares.</param>
    /// <param name="settings">The parsed settings for this command.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    protected abstract Task<int> RunAsync(CliContext context, TSettings settings, CancellationToken cancellationToken);
}
