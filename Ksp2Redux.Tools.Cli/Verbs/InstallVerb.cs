using System;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Installs a published version, or a local patch file, into a KSP2 install.
/// </summary>
public static class InstallVerb
{
    private const long PROGRESS_STEP_BYTES = 32L * 1024 * 1024;

    /// <summary>
    /// Plans the route from the installed version to the requested one and applies it.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <param name="options">The parsed options for this verb.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    public static async Task<int> RunAsync(CliContext context, InstallOptions options, CancellationToken cancellationToken)
    {
        var hasVersion = !string.IsNullOrWhiteSpace(options.Version);
        var hasPatchFile = !string.IsNullOrWhiteSpace(options.PatchFile);

        if (hasVersion == hasPatchFile)
        {
            return context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "Name exactly one of a version to install or a local patch file to apply.");
        }

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

        var (plan, target, failure) = hasPatchFile
            ? PlanFromPatchFile(options.PatchFile!)
            : await PlanFromChannel(context, options, entry, install);

        if (failure is { } code)
        {
            return code;
        }

        if (plan!.Steps.Count == 0)
        {
            return context.Output.Fail(
                ExitCode.INSTALL_FAILED,
                target is null
                    ? "No route to the requested patch file from this install."
                    : $"No route from the installed version to {CliContext.FormatVersion(target)}.");
        }

        return await ApplyPlan(context, plan, install, target, cancellationToken);
    }

    // A patch file is applied from stock, so the plan reverts and prepatches first. This mirrors
    // what the launcher does for a hand supplied patch.
    private static (InstallPlan? Plan, GameVersion? Target, int? Failure) PlanFromPatchFile(string patchFile)
    {
        var plan = new InstallPlan();
        plan.ApplyPatchFile(patchFile);
        plan.Prepatch();
        plan.RevertToStock();
        return (plan, null, null);
    }

    private static async Task<(InstallPlan? Plan, GameVersion? Target, int? Failure)> PlanFromChannel(
        CliContext context,
        InstallOptions options,
        Ksp2InstallEntry entry,
        Ksp2Install install)
    {
        var channel = CliContext.ResolveChannel(options.Channel, entry);
        if (channel is null)
        {
            return (null, null, context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "No channel to install from. Name a channel, or configure one on the install."));
        }

        var loaded = await context.LoadFeedsAsync();
        if (!context.FeedService.ReleasesFeed.TryGetValue(channel, out var feed))
        {
            return (null, null, context.FailFeedNotConfigured(channel, loaded));
        }

        var target = CliContext.FindVersion(feed.GetAllVersions(), options.Version!);
        if (target is null)
        {
            return (null, null, context.FailVersionNotFound(options.Version!, channel));
        }

        if (install.GameVersion is null)
        {
            return (null, null, context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"Could not read the installed version out of {entry.ExePath}: {install.VersionDetectionException?.Message ?? "unknown reason"}"));
        }

        context.Output.Progress($"Planning {CliContext.FormatVersion(install.GameVersion)} -> {CliContext.FormatVersion(target)} on channel {channel}.");
        return (feed.GetPatchListToVersion(install.GameVersion, target), target, null);
    }

    private static async Task<int> ApplyPlan(
        CliContext context,
        InstallPlan plan,
        Ksp2Install install,
        GameVersion? target,
        CancellationToken cancellationToken)
    {
        context.InstallPlanService.Describe(plan, context.Output.Progress);

        var lastReportedBytes = 0L;
        try
        {
            await context.InstallPlanService.ApplyToFolder(
                plan,
                install.InstallDir,
                context.Output.Progress,
                ReportDownload,
                ReportSteps,
                cancellationToken);
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

        // Byte level progress arrives every 100ms from the downloader, which is far too chatty for a
        // log file. Report on crossing a size boundary instead, plus the final byte.
        void ReportDownload(long current, long total)
        {
            if (current < total && current - lastReportedBytes < PROGRESS_STEP_BYTES)
            {
                return;
            }

            lastReportedBytes = current;
            var currentMb = current / 1024d / 1024d;
            var totalMb = total / 1024d / 1024d;
            context.Output.Progress($"  downloaded {currentMb:F0} of {totalMb:F0} MB");
        }

        void ReportSteps(int current, int total)
        {
            lastReportedBytes = 0;
            context.Output.Progress($"step {current} of {total}");
        }
    }
}
