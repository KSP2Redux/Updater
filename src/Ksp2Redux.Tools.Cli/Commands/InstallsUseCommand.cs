using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Picks the install every other command acts on by default.
/// </summary>
public sealed class InstallsUseCommand : ReduxCommand<InstallsUseSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsUseSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        context.InstallService.SetActiveInstall(entry.Id);

        if (!context.ConfigPersisted(config => config.ActiveKsp2InstallId == entry.Id))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"The active install could not be written to the launcher config at {context.ConfigService.Config.StoragePath}."));
        }

        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name = entry.Name,
                channel = entry.ReleaseChannel,
                exePath = entry.ExePath,
            },
            () =>
            {
                context.Output.Result(entry.Name);
                context.Output.Detail($"  {entry.Id}  [{entry.ReleaseChannel}]  {entry.ExePath}");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
