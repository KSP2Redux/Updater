using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Installs the newest version published to a KSP2 install's channel.
/// </summary>
public sealed class UpdateCommand : ReduxCommand<UpdateSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        UpdateSettings settings,
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

        if (install.GameVersion is null)
        {
            return context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"Could not read the installed version out of {entry.ExePath}: {install.VersionDetectionException?.Message ?? "unknown reason"}");
        }

        var channel = CliContext.ResolveChannel(settings.Channel, entry);
        if (channel is null)
        {
            return context.Output.Fail(
                ExitCode.USAGE_ERROR,
                "No channel to update from. Name a channel, or configure one on the install.");
        }

        var loaded = await context.LoadFeedsAsync();
        if (!context.FeedService.ReleasesFeed.TryGetValue(channel, out var feed))
        {
            return context.FailFeedNotConfigured(channel, loaded);
        }

        // Newest is the last entry in the same ordering the list command prints, so the two commands
        // can never disagree about which build is the latest one.
        var newest = feed.GetAllVersions()
            .Distinct()
            .OrderBy(v => v.ReleasedAt)
            .LastOrDefault();

        if (newest is null)
        {
            return context.Output.Fail(ExitCode.VERSION_NOT_FOUND, $"Channel '{channel}' has published no versions.");
        }

        var installed = CliContext.FormatVersion(install.GameVersion);
        var latest = CliContext.FormatVersion(newest);

        if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
        {
            context.Output.Heading($"'{entry.Name}' is already on {latest}, the newest build in {channel}.");
            context.Output.Payload(
                new { ok = true, updated = false, installed, latest, channel },
                () => context.Output.Result(installed));

            return ExitCode.SUCCESS;
        }

        var plan = feed.GetPatchListToVersion(install.GameVersion, newest);

        if (settings.IsDryRun)
        {
            context.Output.Heading($"{installed} -> {latest} on channel {channel}.");
            return InstallWorkflow.Describe(context, plan, install.GameVersion, newest);
        }

        if (plan.Steps.Count == 0)
        {
            return context.Output.Fail(ExitCode.INSTALL_FAILED, $"No route from {installed} to {latest}.");
        }

        context.Output.Heading($"Updating {installed} -> {latest} on channel {channel}.");
        return await InstallWorkflow.ApplyAsync(context, plan, install, newest, cancellationToken);
    }
}
