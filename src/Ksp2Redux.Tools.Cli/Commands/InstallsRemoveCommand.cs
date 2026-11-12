using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Removes a KSP2 install from the launcher config, leaving the game files alone.
/// </summary>
public sealed class InstallsRemoveCommand : ReduxCommand<InstallsRemoveSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsRemoveSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        var answer = CliConfirm.Ask(
            context.Output,
            settings.AssumeYes,
            $"Remove '{entry.Name}' from the launcher config? The game files are left alone.",
            requireAnswer: true
        );

        switch (answer)
        {
            case ConfirmAnswer.Declined:
                return Task.FromResult(context.Output.Fail(ExitCode.CANCELLED, "Nothing was removed."));
            case ConfirmAnswer.NeedsFlag:
                return Task.FromResult(context.Output.Fail(
                    ExitCode.USAGE_ERROR,
                    $"Refusing to remove '{entry.Name}' without a terminal to confirm on. Pass --yes."));
            case ConfirmAnswer.Approved:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        context.InstallService.RemoveInstall(entry.Id);

        if (!context.ConfigPersisted(config => config.Ksp2Installs.All(e => e.Id != entry.Id)))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"'{entry.Name}' could not be removed from the launcher config at {context.ConfigService.Config.StoragePath}."));
        }

        context.Output.Payload(
            new
            {
                ok = true,
                removed = entry.Id,
                name = entry.Name,
                active = context.InstallService.ActiveEntry?.Id,
            },
            () => context.Output.Result(entry.Id.ToString()));

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
