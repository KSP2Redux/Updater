using System.Diagnostics;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Opens the install, logs, game data or storage folder in the file browser.
/// </summary>
public sealed class OpenCommand : ReduxCommand<OpenSettings>
{
    private const string LOGS_FOLDER = "logs";

    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        OpenSettings settings,
        CancellationToken cancellationToken)
    {
        var target = settings.Folder.Trim().ToLowerInvariant();
        string? path;
        switch (target)
        {
            case "install":
            {
                var entry = context.ResolveInstallEntry(settings.Install);
                if (entry is null) return Task.FromResult(context.FailInstallNotFound(settings.Install));
                path = context.FileSystem.Path.GetDirectoryName(entry.ExePath);
                break;
            }
            case "logs":
                path = context.FileSystem.Path.Combine(context.ConfigService.GetLocalStorageDirectory(), LOGS_FOLDER);
                break;
            case "game-data":
                path = context.GameDataFolderService.Resolve(context.ResolveInstallEntry(settings.Install));
                break;
            case "storage":
                path = context.ConfigService.GetLocalStorageDirectory();
                break;
            default:
                return Task.FromResult(context.Output.Fail(
                    ExitCode.USAGE_ERROR,
                    $"Unknown folder '{settings.Folder}'. Use install, logs, game-data or storage."));
        }

        if (string.IsNullOrWhiteSpace(path) || !context.FileSystem.Directory.Exists(path))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.PATH_NOT_FOUND,
                target == "game-data"
                    ? $"The game data folder does not exist yet: {path ?? "(unknown)"}. KSP2 creates it the first time it runs."
                    : $"The {target} folder does not exist: {path ?? "(unknown)"}"));
        }

        var opened = false;
        if (!settings.ShouldPrint)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                opened = true;
            }
            catch (Exception e)
            {
                context.Output.Warn($"Could not open the folder: {e.Message}");
            }
        }

        context.Output.Payload(
            new { ok = true, folder = target, path, opened },
            () => context.Output.Result(path));
        return Task.FromResult(ExitCode.SUCCESS);
    }
}
