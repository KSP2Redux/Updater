using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Scans the well known Steam and Epic locations for a KSP2 install.
/// </summary>
public sealed class DetectCommand : ReduxCommand<DetectSettings>
{
    /// <inheritdoc />
    // The detector answers with the first install it finds rather than a list, so this reports one
    // path or nothing at all.
    protected override Task<int> RunAsync(
        CliContext context,
        DetectSettings settings,
        CancellationToken cancellationToken)
    {
        var exePath = context.DetectorService.DetectKsp2InstallLocation();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                "KSP2 was not found in the usual Steam or Epic locations. Pass the path to installs add instead."));
        }

        context.InstallService.TryLoadKsp2Install();
        var configured = context.InstallService.Entries
            .FirstOrDefault(e => string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

        var install = context.ReadInstall(exePath);

        context.Output.Payload(
            new
            {
                ok = true,
                exePath,
                valid = install is { IsValid: true },
                version = install?.GameVersion is null ? null : CliContext.FormatVersion(install.GameVersion),
                distribution = install?.Distribution.ToString(),
                configuredAs = configured?.Name,
                configuredId = configured?.Id,
            },
            () =>
            {
                context.Output.Result(exePath);
                if (install?.GameVersion is not null)
                {
                    context.Output.Detail($"  version:      {CliContext.FormatVersion(install.GameVersion)}");
                    context.Output.Detail($"  distribution: {install.Distribution}");
                }

                context.Output.Detail(configured is null
                    ? "  not in the launcher config yet, add it with: installs add"
                    : $"  already configured as '{configured.Name}' ({configured.Id})");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
