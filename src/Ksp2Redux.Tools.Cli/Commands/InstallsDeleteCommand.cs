using System.Diagnostics;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Deletes a KSP2 install's game files from disk and removes its profile, the CLI's Uninstall KSP2.
/// </summary>
public sealed class InstallsDeleteCommand : ReduxCommand<InstallsDeleteSettings>
{
    private const string STEAM_UNINSTALL_URL = "steam://uninstall/954850";

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        InstallsDeleteSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return context.FailInstallNotFound(settings.Install);
        }

        var removal = context.GameUninstallService.Inspect(entry.ExePath);
        switch (removal.Kind)
        {
            case Ksp2GameRemovalKind.NotAGameFolder:
                return context.Output.Fail(
                    ExitCode.INSTALL_NOT_FOUND,
                    $"{removal.Folder} does not look like a KSP2 install folder (there is no {Ksp2Install.KSP2_EXE_NAME} " +
                    "with KSP2_x64_Data beside it), so nothing was deleted. Use 'installs remove' if the profile points at the wrong place.");

            case Ksp2GameRemovalKind.Missing:
                return Confirmed(context, settings, $"The game folder for '{entry.Name}' is already gone. Remove the profile?") is { } missingRefusal
                    ? missingRefusal
                    : RemoveProfile(context, entry, removal, deleted: false);

            case Ksp2GameRemovalKind.UninstallThroughSteam:
                if (Confirmed(context, settings,
                        $"'{entry.Name}' is managed by Steam, so Steam will uninstall it and ask you to confirm there. " +
                        "Remove the profile and hand the uninstall to Steam?") is { } steamRefusal)
                {
                    return steamRefusal;
                }

                try
                {
                    Process.Start(new ProcessStartInfo(STEAM_UNINSTALL_URL) { UseShellExecute = true });
                }
                catch (Exception e)
                {
                    return context.Output.Fail(ExitCode.USAGE_ERROR, $"Could not open Steam to uninstall KSP2: {e.Message}");
                }

                return RemoveProfile(context, entry, removal, deleted: false);

            case Ksp2GameRemovalKind.DeleteFolder:
            default:
                break;
        }

        if (Confirmed(context, settings,
                $"Delete {removal.Folder} and remove the '{entry.Name}' profile? The whole folder goes, Redux included, " +
                "and this cannot be undone. Saves and settings are kept.") is { } refusal)
        {
            return refusal;
        }

        try
        {
            await context.Output.StatusAsync($"Deleting {removal.Folder}", async _ =>
            {
                await context.GameUninstallService.DeleteAsync(removal);
                return true;
            });
        }
        catch (Exception e)
        {
            return context.Output.Fail(
                ExitCode.INSTALL_FAILED,
                $"Could not finish deleting {removal.Folder}: {e.Message} Make sure the game is not running, then try again.");
        }

        return RemoveProfile(context, entry, removal, deleted: true);
    }

    private static int? Confirmed(CliContext context, InstallsDeleteSettings settings, string question) =>
        CliConfirm.Ask(context.Output, settings.AssumeYes, question, requireAnswer: true) switch
        {
            ConfirmAnswer.Approved => null,
            ConfirmAnswer.Declined => context.Output.Fail(ExitCode.CANCELLED, "Nothing was deleted."),
            _ => context.Output.Fail(ExitCode.USAGE_ERROR, "Refusing to delete a game without a terminal to confirm on. Pass --yes."),
        };

    private static int RemoveProfile(CliContext context, Ksp2InstallEntry entry, Ksp2GameRemoval removal, bool deleted)
    {
        context.InstallService.RemoveInstall(entry.Id);

        if (!context.ConfigPersisted(config => config.Ksp2Installs.All(e => e.Id != entry.Id)))
        {
            return context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"'{entry.Name}' could not be removed from the launcher config at {context.ConfigService.Config.StoragePath}.");
        }

        var handedToSteam = removal.Kind == Ksp2GameRemovalKind.UninstallThroughSteam;
        context.Output.Payload(
            new
            {
                ok = true,
                removed = entry.Id,
                name = entry.Name,
                folder = removal.Folder,
                deleted,
                handedToSteam,
                active = context.InstallService.ActiveEntry?.Id,
            },
            () =>
            {
                context.Output.Result(entry.Id.ToString());
                context.Output.Detail(deleted ? $"  deleted {removal.Folder}"
                    : handedToSteam ? "  Steam is uninstalling the game files"
                    : "  the game folder was already gone");
                context.Output.Detail($"  removed the '{entry.Name}' profile");
            });

        return ExitCode.SUCCESS;
    }
}
