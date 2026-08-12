using Ksp2Redux.Tools.Launcher.Models;
using Spectre.Console;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The steps install and update share once a plan has been worked out.
/// </summary>
public static class InstallWorkflow
{
    /// <summary>
    /// Prints what the plan would do without touching the install.
    /// </summary>
    /// <param name="context">The setup every command shares.</param>
    /// <param name="plan">The plan that would be applied.</param>
    /// <param name="current">The version installed now, or null when it could not be read.</param>
    /// <param name="target">The version the plan installs, or null for a local patch file.</param>
    /// <returns>Success, because describing a plan cannot fail.</returns>
    public static int Describe(CliContext context, InstallPlan plan, GameVersion? current, GameVersion? target)
    {
        List<string> steps = [];
        context.InstallPlanService.Describe(plan, steps.Add);

        context.Output.Payload(
            new
            {
                ok = true,
                dryRun = true,
                from = current is null ? null : CliContext.FormatVersion(current),
                to = target is null ? null : CliContext.FormatVersion(target),
                changesNeeded = plan.Steps.Count > 0,
                steps,
            },
            () => WriteSteps(context, steps, current, target));

        return ExitCode.SUCCESS;
    }

    // The steps are a route rather than a list, so a terminal gets them as a tree hanging off the
    // version being installed. A pipe keeps the flat lines it has always had.
    private static void WriteSteps(CliContext context, IReadOnlyList<string> steps, GameVersion? current, GameVersion? target)
    {
        if (!context.Output.Capabilities.FancyResults)
        {
            foreach (var step in steps)
            {
                context.Output.Result(step);
            }

            return;
        }

        var from = current is null ? "unknown" : CliContext.FormatVersion(current);
        var to = target is null ? "the patch file" : CliContext.FormatVersion(target);

        Tree tree = new($"[{CliTheme.HEADER_STYLE}]{Markup.Escape(from)} -> {Markup.Escape(to)}[/]")
        {
            Style = new Style(CliTheme.SECONDARY),
        };

        foreach (var step in steps)
        {
            tree.AddNode(Markup.Escape(step.Trim()));
        }

        if (steps.Count == 0)
        {
            tree.AddNode($"[{CliTheme.DETAIL_STYLE}]nothing to do[/]");
        }

        context.Output.ResultConsole.Write(tree);
    }

    /// <summary>
    /// Applies the plan, reporting progress and mapping every failure to an exit code.
    /// </summary>
    /// <param name="context">The setup every command shares.</param>
    /// <param name="plan">The plan to apply.</param>
    /// <param name="install">The install the plan is applied to.</param>
    /// <param name="target">The version the plan installs, or null for a local patch file.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> ApplyAsync(
        CliContext context,
        InstallPlan plan,
        Ksp2Install install,
        GameVersion? target,
        CancellationToken cancellationToken)
    {
        context.InstallPlanService.Describe(plan, context.Output.Detail);

        try
        {
            await CliProgressDisplay.RunAsync(
                context.Output,
                (log, download, steps) => context.InstallPlanService.ApplyToFolder(
                    plan,
                    install.InstallDir,
                    log,
                    download,
                    steps,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return context.Output.Fail(ExitCode.CANCELLED, "Install cancelled. The install may be in a partial state.");
        }
        catch (InstallFailedException e)
        {
            return context.Output.Fail(ExitCode.INSTALL_FAILED, e.Message);
        }
        catch (Exception e)
        {
            return context.Output.Fail(
                ExitCode.INSTALL_FAILED,
                $"{e.Message} The install may be in an invalid state, try uninstalling and reinstalling.");
        }

        context.InstallService.TryLoadKsp2Install();
        var installed = context.InstallService.Ksp2?.GameVersion;

        context.Output.Payload(
            new
            {
                ok = true,
                requested = target is null ? null : CliContext.FormatVersion(target),
                installed = installed is null ? null : CliContext.FormatVersion(installed),
                buildNumber = installed?.BuildNumber,
                installDir = install.InstallDir,
            },
            () => context.Output.Result(installed is null
                ? "installed"
                : CliContext.FormatVersion(installed)));

        return ExitCode.SUCCESS;
    }
}
