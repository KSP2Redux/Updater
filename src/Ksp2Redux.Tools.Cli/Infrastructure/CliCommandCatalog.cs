using System.Reflection;
using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// One command as the completion scripts see it.
/// </summary>
/// <param name="Path">The command words, such as "installs add".</param>
/// <param name="SettingsType">The settings the command is parsed into.</param>
/// <param name="Aliases">Other names the command answers to.</param>
public sealed record CliCommandInfo(string Path, Type SettingsType, params string[] Aliases);

/// <summary>
/// The command list the completion scripts are generated from.
/// </summary>
public static class CliCommandCatalog
{
    /// <summary>
    /// Gets every command the CLI answers to, in the order the help screen lists them.
    /// </summary>
    public static IReadOnlyList<CliCommandInfo> Commands { get; } =
    [
        new("channels", typeof(ChannelsSettings)),
        new("installs", typeof(InstallsSettings), "list-installs"),
        new("installs add", typeof(InstallsAddSettings)),
        new("installs remove", typeof(InstallsRemoveSettings)),
        new("installs use", typeof(InstallsUseSettings)),
        new("installs rename", typeof(InstallsRenameSettings)),
        new("installs set-channel", typeof(InstallsChannelSettings)),
        new("detect", typeof(DetectSettings)),
        new("list", typeof(ListSettings)),
        new("current", typeof(CurrentSettings)),
        new("install", typeof(InstallSettings)),
        new("update", typeof(UpdateSettings)),
        new("uninstall", typeof(UninstallSettings)),
        new("launch", typeof(LaunchSettings)),
        new("doctor", typeof(DoctorSettings)),
        new("cache", typeof(CacheSettings)),
        new("cache clear", typeof(CacheClearSettings)),
        new("logs", typeof(LogsSettings)),
        new("version", typeof(VersionSettings)),
        new("self-update", typeof(SelfUpdateSettings)),
        new("self-uninstall", typeof(SelfUninstallSettings)),
        new("completion", typeof(CompletionSettings)),
    ];

    /// <summary>
    /// Commands that are registered but never offered, so they stay out of the candidate lists.
    /// </summary>
    private static readonly string[] HIDDEN_COMMANDS = ["complete"];

    /// <summary>
    /// Says whether a word names a command that can start a command line.
    /// </summary>
    /// <param name="word">The word to check.</param>
    /// <returns>True when the word is a top level command name, an alias, or a hidden command.</returns>
    // Hidden commands count here even though completion never offers them, because the caller uses
    // this to tell a command line apart from one that carries only global options.
    public static bool IsTopLevelCommand(string word) =>
        HIDDEN_COMMANDS.Contains(word, StringComparer.OrdinalIgnoreCase)
        || Commands.Any(command =>
            (!command.Path.Contains(' ') && string.Equals(command.Path, word, StringComparison.OrdinalIgnoreCase))
            || command.Aliases.Contains(word, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Works out what could come next on a command line.
    /// </summary>
    /// <param name="typed">The complete words already on the line, before the cursor.</param>
    /// <param name="partial">The word being completed, which may be empty.</param>
    /// <returns>The candidates, sorted and free of duplicates.</returns>
    // The word under the cursor is passed separately rather than as the last of the words, because
    // an empty one does not survive the trip through the shell and the argument parser.
    public static IReadOnlyList<string> Candidates(IReadOnlyList<string> typed, string partial)
    {
        var path = ResolvePath([.. typed.Where(word => !word.StartsWith('-'))]);

        var candidates = partial.StartsWith('-')
            ? Options(path)
            : Children(path);

        return
        [
            .. candidates
                .Where(candidate => candidate.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Lists the options a command takes, including the ones every command shares.
    /// </summary>
    /// <param name="path">The command words, or an empty string for the root.</param>
    /// <returns>The option names, such as "--json".</returns>
    public static IReadOnlyList<string> Options(string path)
    {
        var settingsType = Commands.FirstOrDefault(command => command.Path == path)?.SettingsType
                           ?? typeof(BaseSettings);

        List<string> options = ["--help"];
        foreach (var property in settingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<CommandOptionAttribute>();
            if (attribute is null)
            {
                continue;
            }

            options.AddRange(attribute.LongNames.Select(name => $"--{name}"));
            options.AddRange(attribute.ShortNames.Select(name => $"-{name}"));
        }

        return options;
    }

    private static IReadOnlyList<string> Children(string path)
    {
        var prefix = path.Length == 0 ? "" : path + " ";

        List<string> children = [];
        foreach (var command in Commands)
        {
            if (!command.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = command.Path[prefix.Length..];
            if (remainder.Length == 0 || remainder.Contains(' '))
            {
                continue;
            }

            children.Add(remainder);
            if (path.Length == 0)
            {
                children.AddRange(command.Aliases);
            }
        }

        if (path.Length == 0)
        {
            children.Add("help");
        }

        return children;
    }

    private static string ResolvePath(IReadOnlyList<string> typed)
    {
        var path = "";
        foreach (var word in typed)
        {
            var candidate = path.Length == 0 ? word : $"{path} {word}";
            var match = Commands.FirstOrDefault(command =>
                string.Equals(command.Path, candidate, StringComparison.OrdinalIgnoreCase)
                || (path.Length == 0 && command.Aliases.Contains(word, StringComparer.OrdinalIgnoreCase)));

            if (match is null)
            {
                break;
            }

            path = match.Path;
        }

        return path;
    }
}
