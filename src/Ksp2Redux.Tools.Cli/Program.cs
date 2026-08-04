using CommandLine;
using Ksp2Redux.Tools.Cli.Verbs;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ksp2Redux.Tools.Cli;

/// <summary>
/// Entry point for the launcher CLI.
/// </summary>
public static class Program
{
    /// <summary>
    /// Parses the command line and runs the requested verb.
    /// </summary>
    /// <param name="args">The raw process arguments.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> Main(string[] args)
    {
        // Help and parse errors go to stderr so stdout stays the data channel in every invocation.
        var parser = new Parser(settings =>
        {
            settings.HelpWriter = Console.Error;
            settings.CaseInsensitiveEnumValues = true;
        });

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var parsed = parser.ParseArguments<
            ChannelsOptions,
            InstallsOptions,
            ListOptions,
            CurrentOptions,
            InstallOptions,
            UninstallOptions,
            LaunchOptions>(args);

        try
        {
            return await parsed.MapResult(
                (ChannelsOptions o) => Run(o, ctx => ChannelsVerb.RunAsync(ctx)),
                (InstallsOptions o) => Run(o, ctx => InstallsVerb.RunAsync(ctx)),
                (ListOptions o) => Run(o, ctx => ListVerb.RunAsync(ctx, o)),
                (CurrentOptions o) => Run(o, ctx => CurrentVerb.RunAsync(ctx, o)),
                (InstallOptions o) => Run(o, ctx => InstallVerb.RunAsync(ctx, o, cancellation.Token)),
                (UninstallOptions o) => Run(o, ctx => UninstallVerb.RunAsync(ctx, o, cancellation.Token)),
                (LaunchOptions o) => Run(o, ctx => LaunchVerb.RunAsync(ctx, o, cancellation.Token)),
                _ => Task.FromResult(ExitCode.USAGE_ERROR));
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: cancelled.");
            return ExitCode.CANCELLED;
        }
    }

    // The launcher's LogService writes its banner and every log line to Console.Out, which would
    // corrupt a JSON document and interleave with results in text mode. Capture the real stdout for
    // results, then point Console.Out at stderr so any launcher write lands there instead.
    private static async Task<int> Run(BaseOptions options, Func<CliContext, Task<int>> verb)
    {
        var results = Console.Out;
        Console.SetOut(Console.Error);

        var output = new CliOutput(results, options.IsJson);
        try
        {
            var services = CliServiceProvider.Build(output);

            // LogService writes a session header to the console from its constructor, bypassing the
            // level gate below. That header belongs in the log file, so swallow the console copy by
            // muting Console.Out across the one resolve that constructs it.
            if (!options.IsVerbose)
            {
                Console.SetOut(TextWriter.Null);
            }

            var log = services.GetRequiredService<ILogService>();
            Console.SetOut(Console.Error);

            // The launcher logs at Info, which is right for a session that runs for hours behind a
            // window and far too chatty for a command that runs for seconds. Warn and above still
            // reaches stderr, and the full log still reaches the log file either way.
            log.MinimumLevel = options.IsVerbose ? LogLevel.Debug : LogLevel.Warn;

            var context = new CliContext(services, output);
            return await verb(context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return output.Fail(ExitCode.USAGE_ERROR, e.Message);
        }
    }
}
