using System;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Removes Redux from a KSP2 install, returning it to stock.
/// </summary>
public static class UninstallVerb
{
    /// <summary>
    /// Applies an uninstall plan to the selected install.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <param name="options">The parsed options for this verb.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> RunAsync(CliContext context, UninstallOptions options, CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(options.Install);
        if (entry is null)
        {
            return context.FailInstallNotFound(options.Install);
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
            context.Output.Progress($"{entry.Name} is already {install.Distribution}, nothing to remove.");
            context.Output.Payload(new { ok = true, removed = false, distribution = install.Distribution.ToString() },
                () => context.Output.Result("stock"));
            return ExitCode.SUCCESS;
        }

        var plan = new InstallPlan();
        plan.Uninstall();
        context.InstallPlanService.Describe(plan, context.Output.Progress);

        try
        {
            await context.InstallPlanService.ApplyToFolder(
                plan,
                install.InstallDir,
                context.Output.Progress,
                (_, _) => { },
                (current, total) => context.Output.Progress($"step {current} of {total}"),
                cancellationToken);
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
