using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Steam;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Downloads the signed-in player's own copy of KSP2 from Steam and adds it as an install profile.
/// </summary>
// Bypasses the Steam client, which will not download KSP2 on macOS.
public sealed class SteamDownloadCommand : ReduxCommand<SteamDownloadSettings>
{
    private const string DEFAULT_PROFILE_NAME = "KSP2 (Steam download)";
    private const string GAMES_FOLDER = "Games";

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        SteamDownloadSettings settings,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(context, settings.Folder);

        var session = context.SteamSession;
        if (!session.HasSavedLogin)
        {
            return context.Output.Fail(
                ExitCode.STEAM_NOT_SIGNED_IN,
                "Downloading KSP2 needs your Steam account. Run 'redux-launcher-cli steam login' first.");
        }

        var connected = await context.Output.StatusAsync("Connecting to Steam", _ => session.TryResumeAsync(cancellationToken));
        if (!connected)
        {
            return session.HasSavedLogin
                ? context.Output.Fail(ExitCode.STEAM_FAILED, "Steam could not be reached. Check your connection and try again.")
                : context.Output.Fail(ExitCode.STEAM_NOT_SIGNED_IN,
                    "Steam no longer accepts the saved login. Run 'redux-launcher-cli steam login' to sign in again.");
        }

        IReadOnlyList<SteamDepot> depots;
        try
        {
            depots = await context.Output.StatusAsync("Looking up KSP2 on Steam",
                _ => context.SteamDownloader.GetDepotsAsync(SteamDepotDownloader.KSP2_APP_ID, cancellationToken));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return context.Output.Fail(ExitCode.DOWNLOAD_FAILED, e.Message);
        }

        var size = depots.Aggregate(0UL, (total, depot) => total + depot.Size);
        context.Output.Heading($"Kerbal Space Program 2 for {session.Account?.DisplayName}: {CliFormat.Bytes((long)size)}");
        context.Output.Detail($"  into {target}");
        WarnIfShortOfSpace(context, target, size);

        switch (CliConfirm.Ask(context.Output, settings.AssumeYes, $"Download {CliFormat.Bytes((long)size)} into {target}?", requireAnswer: true))
        {
            case ConfirmAnswer.Declined:
                return context.Output.Fail(ExitCode.CANCELLED, "Nothing was downloaded.");
            case ConfirmAnswer.NeedsFlag:
                return context.Output.Fail(ExitCode.USAGE_ERROR, "Refusing to start a download this size without a terminal to confirm on. Pass --yes.");
            case ConfirmAnswer.Approved:
            default:
                break;
        }

        try
        {
            context.FileSystem.Directory.CreateDirectory(target);

            var animated = context.Output.Capabilities.CanAnimate;
            string? lastStage = null;
            await CliProgressDisplay.RunAsync(context.Output, (log, bytes, _) =>
                context.SteamDownloader.DownloadAsync(
                    SteamDepotDownloader.KSP2_APP_ID,
                    target,
                    new ImmediateProgress<SteamDownloadProgress>(progress =>
                    {
                        if (animated)
                        {
                            log($"{progress.Stage}  {progress.FilesDone}/{progress.FilesTotal} files");
                        }
                        else if (progress.Stage != lastStage)
                        {
                            lastStage = progress.Stage;
                            log(progress.Stage);
                        }

                        if (progress.TotalBytes > 0)
                        {
                            bytes(progress.DownloadedBytes, progress.TotalBytes);
                        }
                    }),
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return context.Output.Fail(
                ExitCode.CANCELLED,
                "Download stopped. Run the same command again to pick up where it left off.");
        }
        catch (Exception e)
        {
            return context.Output.Fail(
                ExitCode.DOWNLOAD_FAILED,
                $"The download failed: {e.Message} Run the same command again to pick up where it left off.");
        }

        var exePath = context.FileSystem.Path.Combine(target, Ksp2Install.KSP2_EXE_NAME);
        if (!context.FileSystem.File.Exists(exePath))
        {
            return context.Output.Fail(
                ExitCode.DOWNLOAD_FAILED,
                $"The download finished but {Ksp2Install.KSP2_EXE_NAME} is missing from {target}.");
        }

        return RegisterProfile(context, settings, exePath);
    }

    private static int RegisterProfile(CliContext context, SteamDownloadSettings settings, string exePath)
    {
        context.InstallService.TryLoadKsp2Install();
        var entry = context.InstallService.Entries
            .FirstOrDefault(e => string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
        var added = entry is null;
        entry ??= context.InstallService.AddInstall(exePath, string.IsNullOrWhiteSpace(settings.Name) ? DEFAULT_PROFILE_NAME : settings.Name.Trim());

        if (added && !string.IsNullOrWhiteSpace(settings.Channel))
        {
            context.InstallService.UpdateInstallReleaseChannel(entry.Id, settings.Channel.Trim());
        }

        context.InstallService.SetActiveInstall(entry.Id);

        var id = entry.Id;
        if (!context.ConfigPersisted(config => config.ActiveKsp2InstallId == id && config.Ksp2Installs.Any(e => e.Id == id)))
        {
            return context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"KSP2 was downloaded, but the profile could not be written to the launcher config at {context.ConfigService.Config.StoragePath}.");
        }

        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name = entry.Name,
                exePath = entry.ExePath,
                channel = entry.ReleaseChannel,
                added,
                active = true,
            },
            () =>
            {
                context.Output.Result(entry.Id.ToString());
                context.Output.Detail($"  {(added ? "added" : "updated")} the '{entry.Name}' profile and made it active");
                context.Output.Detail("  next: 'redux-launcher-cli update' installs Redux into it");
            });

        return ExitCode.SUCCESS;
    }

    /// <summary>
    /// Resolves the folder a download goes to.
    /// </summary>
    /// <param name="context">The context supplying the file system and home folder.</param>
    /// <param name="folder">The folder the user gave, or null for ~/Games.</param>
    /// <returns>An absolute "Kerbal Space Program 2" folder.</returns>
    // A quoted "~/Games" reaches the command unexpanded.
    internal static string ResolveTarget(CliContext context, string? folder)
    {
        var fileSystem = context.FileSystem;
        var home = context.EnvironmentProvider.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var chosen = string.IsNullOrWhiteSpace(folder) ? fileSystem.Path.Combine(home, GAMES_FOLDER) : folder.Trim().Trim('"');

        if (chosen == "~")
        {
            chosen = home;
        }
        else if (chosen.StartsWith("~/", StringComparison.Ordinal) || chosen.StartsWith("~\\", StringComparison.Ordinal))
        {
            chosen = fileSystem.Path.Combine(home, chosen[2..]);
        }

        return SteamDepotDownloader.GameFolderIn(fileSystem, fileSystem.Path.GetFullPath(chosen));
    }

    // Only warns, as files already in the folder count towards the size.
    private static void WarnIfShortOfSpace(CliContext context, string target, ulong size)
    {
        var probe = target;
        while (!context.FileSystem.Directory.Exists(probe) && context.FileSystem.Path.GetDirectoryName(probe) is { Length: > 0 } parent)
        {
            probe = parent;
        }

        if (context.DiskSpaceService.GetAvailableFreeSpace(probe) is { } free && (ulong)free < size)
        {
            context.Output.Warn($"Only {CliFormat.Bytes(free)} is free there. The download may run out of space.");
        }
    }

    // Progress<T> posts to the thread pool, which reorders reports.
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
