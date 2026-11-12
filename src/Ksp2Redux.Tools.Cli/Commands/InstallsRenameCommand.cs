using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Renames a KSP2 install in the launcher config.
/// </summary>
public sealed class InstallsRenameCommand : ReduxCommand<InstallsRenameSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsRenameSettings settings,
        CancellationToken cancellationToken)
    {
        var name = settings.Name.Trim();
        if (name.Length == 0)
        {
            return Task.FromResult(context.Output.Fail(ExitCode.USAGE_ERROR, "The new name cannot be blank."));
        }

        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        var previous = entry.Name;
        context.InstallService.RenameInstall(entry.Id, name);

        if (!context.ConfigPersisted(config => config.Ksp2Installs.Any(e => e.Id == entry.Id && e.Name == name)))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"The new name could not be written to the launcher config at {context.ConfigService.Config.StoragePath}."));
        }

        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name,
                previousName = previous,
            },
            () => context.Output.Result(name));

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
