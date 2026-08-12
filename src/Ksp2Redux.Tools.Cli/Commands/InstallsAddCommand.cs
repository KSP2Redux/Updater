using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Adds a KSP2 install to the launcher config.
/// </summary>
public sealed class InstallsAddCommand : ReduxCommand<InstallsAddSettings>
{
    /// <inheritdoc />
    protected override Task<int> RunAsync(
        CliContext context,
        InstallsAddSettings settings,
        CancellationToken cancellationToken)
    {
        var requested = settings.Path ?? context.DetectorService.DetectKsp2InstallLocation();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                "No path given and KSP2 could not be found in the usual Steam or Epic locations."));
        }

        var exePath = context.ResolveExePath(requested);
        var install = context.ReadInstall(exePath);
        if (install is not { IsValid: true })
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"{exePath} is not a KSP2 install."));
        }

        context.InstallService.TryLoadKsp2Install();
        var existing = context.InstallService.Entries
            .FirstOrDefault(e => string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.USAGE_ERROR,
                $"{exePath} is already configured as '{existing.Name}' ({existing.Id})."));
        }

        var entry = context.InstallService.AddInstall(exePath, settings.Name);

        if (!string.IsNullOrWhiteSpace(settings.Channel))
        {
            context.InstallService.UpdateInstallReleaseChannel(entry.Id, settings.Channel.Trim());
        }

        if (settings.ShouldActivate)
        {
            context.InstallService.SetActiveInstall(entry.Id);
        }

        if (!context.ConfigPersisted(config => config.Ksp2Installs.Any(e => e.Id == entry.Id)))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.CONFIG_WRITE_FAILED,
                $"'{entry.Name}' could not be written to the launcher config at {context.ConfigService.Config.StoragePath}."));
        }

        var isActive = context.InstallService.ActiveEntry?.Id == entry.Id;
        context.Output.Payload(
            new
            {
                ok = true,
                id = entry.Id,
                name = entry.Name,
                exePath = entry.ExePath,
                channel = entry.ReleaseChannel,
                active = isActive,
                version = install.GameVersion is null ? null : CliContext.FormatVersion(install.GameVersion),
            },
            () =>
            {
                context.Output.Result(entry.Id.ToString());
                context.Output.Detail($"  name:     {entry.Name}");
                context.Output.Detail($"  channel:  {entry.ReleaseChannel}");
                context.Output.Detail($"  path:     {entry.ExePath}");
                context.Output.Detail($"  active:   {isActive}");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
