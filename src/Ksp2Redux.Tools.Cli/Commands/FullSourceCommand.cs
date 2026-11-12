using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Drives the production full-source coordinator without a local-file authentication bypass.
/// </summary>
public class FullSourceCommand : ReduxCommand<FullSourceSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(CliContext context, FullSourceSettings settings, CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
            return context.FailInstallNotFound(settings.Install);
        IFullSourceInstallationService coordinator = context.FullSourceInstallationService
            ?? throw new InvalidOperationException("The full-source coordinator is not registered.");
        string installRoot = context.FileSystem.Path.GetDirectoryName(entry.ExePath)!;
        FullSourceInstallationStatus? status;
        switch (settings.Operation)
        {
            case "recover":
                status = await Task.Run(() => coordinator.Reconcile(entry.Id, installRoot), cancellationToken);
                break;
            case "uninstall":
                if (CliConfirm.Ask(context.Output, settings.AssumeYes,
                        $"Return '{entry.Name}' to its original stock generation?", requireAnswer: false) == ConfirmAnswer.Declined)
                    return context.Output.Fail(ExitCode.CANCELLED, "Nothing was removed.");
                status = await Task.Run(() => coordinator.UninstallAsync(entry.Id, context.Output.Progress, cancellationToken, installRoot), cancellationToken);
                break;
            case "apply":
            case "repair":
                if (string.IsNullOrWhiteSpace(settings.Release))
                    return context.Output.Fail(ExitCode.USAGE_ERROR, "An independently authenticated release identity is required.");
                string channel = settings.Channel ?? entry.ReleaseChannel;
                bool repair = settings.Operation == "repair";
                bool hasCatalog = coordinator.HasCatalog(installRoot);
                FullSourcePreparedTarget target;
                FullSourceStockPreparation? stock = null;
                if (!hasCatalog)
                {
                    if (repair)
                        throw new InvalidDataException("Initial full-source conversion requires independently verified stock and ownership.");
                    (target, stock) = await context.FullSourcePreparationService.FetchInitialInstallAsync(settings.Release,
                        channel, null, entry.Id, cancellationToken);
                }
                else if (repair)
                {
                    target = await context.FullSourcePreparationService.FetchRepairTargetAsync(settings.Release, channel, null,
                        entry.Id, cancellationToken);
                }
                else if (coordinator.GetPublishedIdentity(entry.Id) is { } installed)
                {
                    target = await context.FullSourcePreparationService.FetchTargetForInstalledReleaseAsync(settings.Release,
                        channel, null, entry.Id, installed, cancellationToken);
                }
                else
                {
                    target = await context.FullSourcePreparationService.FetchTargetAsync(settings.Release, channel, cancellationToken);
                }
                var plan = new InstallPlan
                {
                    FullSourceInstallationId = entry.Id, FullSourceTarget = target,
                    FullSourceStock = stock,
                    IsFullSourceRepair = repair
                };
                await context.InstallPlanService.ApplyToFolder(plan, installRoot,
                    line => context.Output.Progress(line.English), (_, _) => { }, (_, _) => { }, cancellationToken);
                status = coordinator.Reconcile(entry.Id, installRoot);
                break;
            default:
                return context.Output.Fail(ExitCode.USAGE_ERROR, "Choose apply, repair, uninstall or recover.");
        }
        if (status is null)
            return context.Output.Fail(ExitCode.INSTALL_NOT_FOUND, "The installation has no full-source catalog.");
        context.Output.Payload(new
        {
            ok = status.ArchiveFailure is null, installationId = status.InstallationId,
            generation = status.Generation.BaselineIdentity, release = status.Release,
            result = status.TransactionResult?.ToString(), archiveFailure = status.ArchiveFailure?.Message
        }, () => context.Output.Result(status.Generation.BaselineIdentity));
        return status.ArchiveFailure is null ? ExitCode.SUCCESS : ExitCode.INSTALL_FAILED;
    }
}
