using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Installs a published version, or a local patch file, into a KSP2 install.
/// </summary>
public sealed class InstallCommand : ReduxCommand<InstallSettings>
{
    private const int CHOICE_COUNT = 15;

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        InstallSettings settings,
        CancellationToken cancellationToken)
    {
        var hasVersion = !string.IsNullOrWhiteSpace(settings.Version);
        var hasPatchFile = !string.IsNullOrWhiteSpace(settings.PatchFile);

        // Naming both is always a mistake. Naming neither is only a mistake when there is nobody to
        // ask, so on a terminal the versions are offered as a list instead.
        if (hasVersion && hasPatchFile)
        {
            return context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "Name exactly one of a version to install or a local patch file to apply.");
        }

        if (!hasVersion && !hasPatchFile && !context.Output.Capabilities.CanPrompt)
        {
            return context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "Name exactly one of a version to install or a local patch file to apply.");
        }

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

        var (plan, target, failure) = hasPatchFile
            ? PlanFromPatchFile(settings.PatchFile!)
            : await PlanFromChannel(context, settings, entry, install);

        if (failure is { } code)
        {
            return code;
        }

        if (settings.IsDryRun)
        {
            return InstallWorkflow.Describe(context, plan!, install.GameVersion, target);
        }

        if (plan!.Steps.Count == 0)
        {
            return context.Output.Fail(
                ExitCode.INSTALL_FAILED,
                target is null
                    ? "No route to the requested patch file from this install."
                    : $"No route from the installed version to {CliContext.FormatVersion(target)}.");
        }

        return await InstallWorkflow.ApplyAsync(context, plan, install, target, cancellationToken);
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
        InstallSettings settings,
        Ksp2InstallEntry entry,
        Ksp2Install install)
    {
        var channel = CliContext.ResolveChannel(settings.Channel, entry);
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

        if (install.GameVersion is null)
        {
            return (null, null, context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"Could not read the installed version out of {entry.ExePath}: {install.VersionDetectionException?.Message ?? "unknown reason"}"));
        }

        var target = string.IsNullOrWhiteSpace(settings.Version)
            ? Choose(context, feed.GetAllVersions(), install.GameVersion, entry.Name, channel)
            : CliContext.FindVersion(feed.GetAllVersions(), settings.Version);

        if (target is null)
        {
            return string.IsNullOrWhiteSpace(settings.Version)
                ? (null, null, context.Output.Fail(ExitCode.VERSION_NOT_FOUND, $"Channel '{channel}' has published no versions."))
                : (null, null, context.FailVersionNotFound(settings.Version, channel));
        }

        context.Output.Heading($"Planning {CliContext.FormatVersion(install.GameVersion)} -> {CliContext.FormatVersion(target)} on channel {channel}.");
        return (feed.GetPatchListToVersion(install.GameVersion, target), target, null);
    }

    // Only reached on a terminal, where erroring out over a missing argument the user can simply be
    // shown is worse than asking.
    private static GameVersion? Choose(
        CliContext context,
        IEnumerable<GameVersion> versions,
        GameVersion installed,
        string installName,
        string channel)
    {
        var choices = versions
            .Distinct()
            .OrderByDescending(version => version.ReleasedAt)
            .Take(CHOICE_COUNT)
            .ToList();

        if (choices.Count == 0)
        {
            return null;
        }

        var current = CliContext.FormatVersion(installed);

        SelectionPrompt<GameVersion> prompt = new()
        {
            Title = $"[{CliTheme.HEADER_STYLE}]Pick a version to install into {Markup.Escape(installName)} from {Markup.Escape(channel)}:[/]",
            HighlightStyle = new Style(CliTheme.BRAND_ORANGE),
            PageSize = Math.Max(3, Math.Min(CHOICE_COUNT, choices.Count) + 2),
        };

        prompt.UseConverter(version =>
        {
            var formatted = CliContext.FormatVersion(version);
            var label = version.Label is null ? "" : $"  {Markup.Escape(version.Label)}";
            var marker = string.Equals(formatted, current, StringComparison.OrdinalIgnoreCase) ? "  (installed)" : "";
            return $"{Markup.Escape(version.BuildNumber)}  [{CliTheme.DETAIL_STYLE}]{Markup.Escape(formatted)}{label}{marker}[/]";
        });

        prompt.AddChoices(choices);
        return context.Output.ProgressConsole.Prompt(prompt);
    }
}
