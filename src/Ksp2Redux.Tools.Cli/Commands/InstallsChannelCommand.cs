using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Moves a KSP2 install to another release channel.
/// </summary>
public sealed class InstallsChannelCommand : ReduxCommand<InstallsChannelSettings>
{
    private const string BETA_CHANNEL = "beta";

    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsChannelSettings settings,
        CancellationToken cancellationToken)
    {
        var channel = settings.Channel.Trim();
        if (channel.Length == 0)
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "The channel cannot be blank."));
        }

        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        var previous = entry.ReleaseChannel;
        var switchingToBeta = string.Equals(channel, BETA_CHANNEL, StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(previous, BETA_CHANNEL, StringComparison.OrdinalIgnoreCase);

        context.InstallService.UpdateInstallReleaseChannel(entry.Id, channel);

        if (!context.ConfigPersisted(config => config.Ksp2Installs.Any(e => e.Id == entry.Id && e.ReleaseChannel == channel)))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"The channel could not be written to the launcher config at {context.ConfigService.Config.StoragePath}."));
        }

        // The launcher raises this as a dialog when the same switch is made in the settings tab.
        if (switchingToBeta)
        {
            context.Output.WarnPanel(
                "Switching to beta",
                "The beta channel is for QA testing. Builds are unstable and can break at any time.",
                "We recommend not using it with existing campaigns.");
        }

        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name = entry.Name,
                channel,
                previousChannel = previous,
            },
            () => context.Output.Result(channel));

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
