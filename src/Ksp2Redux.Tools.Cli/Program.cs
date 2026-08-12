using System.Reflection;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli;

/// <summary>
/// Entry point for the launcher CLI.
/// </summary>
public static class Program
{
    private const string APPLICATION_NAME = "redux-launcher-cli";

    /// <summary>
    /// Parses the command line and runs the requested command.
    /// </summary>
    /// <param name="args">The raw process arguments.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> Main(string[] args)
    {
        CliCancellation.CancelOnInterrupt();

        // Help and parse errors go to stderr so stdout stays the data channel in every invocation.
        var console = CliConsole.Create(Console.Error, IsStyled(args), isInteractive: false);

        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName(APPLICATION_NAME);
            config.SetApplicationVersion(ApplicationVersion());
            config.ConfigureConsole(console);
            config.SetExceptionHandler((exception, _) => Report(console, exception));
            config.UseStrictParsing();
            config.SetHelpProvider(new CliHelpProvider(config.Settings));

            config.AddExample("install", "103669");
            config.AddExample("list", "--channel", "internal", "--take", "5");

            config.AddCommand<ChannelsCommand>("channels")
                .WithDescription("List the release feeds in the launcher config and the channel each one serves.");

            config.AddBranch("installs", installs =>
                {
                    installs.SetDescription("List and manage the KSP2 installs in the launcher config.");
                    installs.SetDefaultCommand<InstallsCommand>();

                    installs.AddCommand<InstallsAddCommand>("add")
                        .WithDescription("Add a KSP2 install, detecting the path when none is given.")
                        .WithExample("installs", "add", "D:\\Games\\KSP2", "--name", "Testing");

                    installs.AddCommand<InstallsRemoveCommand>("remove")
                        .WithDescription("Remove a KSP2 install from the config, leaving the game files alone.");

                    installs.AddCommand<InstallsUseCommand>("use")
                        .WithDescription("Make a KSP2 install the active one.");

                    installs.AddCommand<InstallsRenameCommand>("rename")
                        .WithDescription("Rename a KSP2 install.");

                    installs.AddCommand<InstallsChannelCommand>("set-channel")
                        .WithDescription("Move a KSP2 install to another release channel.");
                })
                .WithAlias("list-installs");

            config.AddCommand<DetectCommand>("detect")
                .WithDescription("Scan the usual Steam and Epic locations for a KSP2 install.");

            config.AddCommand<ListCommand>("list")
                .WithDescription("List the versions published to a channel, oldest first.")
                .WithExample("list", "--channel", "internal", "--take", "5");

            config.AddCommand<CurrentCommand>("current")
                .WithDescription("Report the Redux version currently installed into a KSP2 install.");

            config.AddCommand<InstallCommand>("install")
                .WithDescription("Install a specific version into a KSP2 install.")
                .WithExample("install", "103669")
                .WithExample("install", "--patch-file", "patch.zip");

            config.AddCommand<UpdateCommand>("update")
                .WithDescription("Install the newest version published to the install's channel.")
                .WithExample("update", "--dry-run");

            config.AddCommand<UninstallCommand>("uninstall")
                .WithDescription("Remove Redux from a KSP2 install, returning it to stock.");

            config.AddCommand<LaunchCommand>("launch")
                .WithDescription("Start KSP2 using the install's configured launch settings.");

            config.AddCommand<DoctorCommand>("doctor")
                .WithDescription("Report the paths, installs and feeds the launcher can see.");

            config.AddBranch("cache", cache =>
            {
                cache.SetDescription("Report and empty the launcher's download cache.");
                cache.SetDefaultCommand<CacheCommand>();

                cache.AddCommand<CacheClearCommand>("clear")
                    .WithDescription("Delete cached manifests and downloads.")
                    .WithExample("cache", "clear", "--older-than", "30");
            });

            config.AddCommand<LogsCommand>("logs")
                .WithDescription("Print the launcher's most recent log file.")
                .WithExample("logs", "--tail", "100");

            config.AddCommand<VersionCommand>("version")
                .WithDescription("Report the CLI's own version, and with --check whether a newer one is published.")
                .WithExample("version", "--check");

            config.AddCommand<SelfUpdateCommand>("self-update")
                .WithDescription("Replace this CLI with the newest published build.")
                .WithExample("self-update", "--check");

            config.AddCommand<SelfUninstallCommand>("self-uninstall")
                .WithDescription("Remove this CLI, leaving the launcher config alone.");

            config.AddCommand<CompletionCommand>("completion")
                .WithDescription("Print a shell completion script for pwsh or bash.")
                .WithExample("completion", "pwsh");

            config.AddCommand<CompleteCommand>("complete")
                .IsHidden();
        });

        var normalized = Normalize(args);
        var wantsVersion = Mentions(normalized, "--version", "-v");
        var wantsHelp = Mentions(normalized, "--help", "-h");
        var hasCommand = normalized.Any(CliCommandCatalog.IsTopLevelCommand);
        var showsHelp = !wantsVersion && (wantsHelp || !hasCommand);

        // The help screen is the CLI's front door, so it gets the logo the launcher window opens
        // with. Every other command draws its own, or nothing at all.
        if (showsHelp && IsStyled(args) && !Mentions(args, "--no-banner"))
        {
            CliBanner.Write(console);
        }

        // Global options carry no command, so they would otherwise be rejected by a root that has
        // no settings of its own rather than answered with the help they were asking for. Help goes
        // first because an unknown option is reported before a later argument is read.
        if (showsHelp && !wantsHelp)
        {
            await app.RunAsync(["--help", .. normalized]);
            return ExitCode.USAGE_ERROR;
        }

        return await app.RunAsync(normalized);
    }

    // CommandLineParser answered to a help verb, so it keeps working here rather than turning into
    // an unknown command for whatever script already calls it. The version verb is a real command.
    private static string[] Normalize(string[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        if (string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            return [.. args.Skip(1), "--help"];
        }

        return args;
    }

    private static bool Mentions(string[] args, params string[] flags) =>
        args.Any(argument => flags.Contains(argument, StringComparer.OrdinalIgnoreCase));

    private static int Report(IAnsiConsole console, Exception exception)
    {
        if (exception is CommandParseException { Pretty: { } pretty })
        {
            console.Write(pretty);
        }
        else
        {
            console.MarkupLine($"[bold {CliTheme.BRAND_RED.ToMarkup()}]error:[/] {Markup.Escape(exception.Message)}");
        }

        console.MarkupLine($"Run [bold]{APPLICATION_NAME} --help[/] to see the available commands.");
        return ExitCode.USAGE_ERROR;
    }

    private static bool IsStyled(string[] args)
    {
        var mode = ColorModeFrom(args);
        return mode switch
        {
            ColorMode.Always => true,
            ColorMode.Never => false,
            _ => !Console.IsErrorRedirected,
        };
    }

    // The colour option is read off the raw arguments because help and parse errors are written
    // before there is a parsed settings object to read it from.
    private static ColorMode ColorModeFrom(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index] switch
            {
                "--color" when index + 1 < args.Length => args[index + 1],
                var argument when argument.StartsWith("--color=", StringComparison.OrdinalIgnoreCase) => argument["--color=".Length..],
                _ => null,
            };

            if (value is not null && Enum.TryParse<ColorMode>(value, ignoreCase: true, out var mode))
            {
                return mode;
            }
        }

        return ColorMode.Auto;
    }

    private static string ApplicationVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
