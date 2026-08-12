using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Removes Redux from a KSP2 install, returning it to stock.
/// </summary>
public sealed class UninstallCommand : ReduxCommand<UninstallSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        UninstallSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return context.FailInstallNotFound(settings.Install);
        }

        var install = context.InstallService.Ksp2;
        if (install is not { IsValid: true })
        {
            return context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"'{entry.Name}' does not point at a valid KSP2 install: {entry.ExePath}");
        }

        if (install.Distribution != Distribution.Redux)
        {
            context.Output.Heading($"{entry.Name} is already {install.Distribution}, nothing to remove.");
            context.Output.Payload(new { ok = true, removed = false, distribution = install.Distribution.ToString() },
                () => context.Output.Result("stock"));
            return ExitCode.SUCCESS;
        }

        // A script that never had a terminal to answer on keeps running unattended, because this
        // command has always done that.
        var answer = CliConfirm.Ask(
            context.Output,
            settings.AssumeYes,
            $"Remove Redux from '{entry.Name}' and return it to stock?",
            requireAnswer: false);

        if (answer == ConfirmAnswer.Declined)
        {
            return context.Output.Fail(ExitCode.CANCELLED, "Nothing was removed.");
        }

        var plan = new InstallPlan();
        plan.Uninstall();
        context.InstallPlanService.Describe(plan, context.Output.Detail);

        try
        {
            await CliProgressDisplay.RunAsync(
                context.Output,
                (log, _, steps) => context.InstallPlanService.ApplyToFolder(
                    plan,
                    install.InstallDir,
                    log,
                    (_, _) => { },
                    steps,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return context.Output.Fail(ExitCode.CANCELLED, "Uninstall cancelled. The install may be in a partial state.");
        }
        catch (InstallFailedException e)
        {
            return context.Output.Fail(ExitCode.INSTALL_FAILED, e.Message);
        }
        catch (Exception e)
        {
            return context.Output.Fail(ExitCode.INSTALL_FAILED, e.Message);
        }

        context.InstallService.TryLoadKsp2Install();
        context.Output.Payload(
            new
            {
                ok = true,
                removed = true,
                distribution = context.InstallService.Ksp2?.Distribution.ToString(),
            },
            () => context.Output.Result("stock"));

        return ExitCode.SUCCESS;
    }
}
