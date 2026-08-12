using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Reports the version currently installed into a KSP2 install.
/// </summary>
public sealed class CurrentCommand : ReduxCommand<CurrentSettings>
{
    /// <inheritdoc />
    // The version comes from reading the install's Assembly-CSharp rather than from the launcher
    // config, so it stays correct after a patch applied by something other than the launcher.
    protected override Task<int> RunAsync(
        CliContext context,
        CurrentSettings settings,
        CancellationToken cancellationToken)
    {
        var entry = context.ResolveInstallEntry(settings.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(settings.Install));
        }

        var install = context.InstallService.Ksp2;
        if (install is not { IsValid: true })
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"'{entry.Name}' does not point at a valid KSP2 install: {entry.ExePath}"));
        }

        if (install.GameVersion is null)
        {
            return Task.FromResult(context.Output.Fail(
                ExitCode.INSTALL_NOT_FOUND,
                $"Could not read a version out of {entry.ExePath}: {install.VersionDetectionException?.Message ?? "unknown reason"}"));
        }

        var version = install.GameVersion;
        context.Output.Payload(
            new
            {
                install = entry.Name,
                installId = entry.Id,
                version = CliContext.FormatVersion(version),
                buildNumber = version.BuildNumber,
                channel = version.Channel,
                commitHash = version.CommitHash,
                distribution = install.Distribution.ToString(),
                installDir = install.InstallDir,
            },
            () =>
            {
                context.Output.Result(CliContext.FormatVersion(version));
                context.Output.Detail($"  install:      {entry.Name} ({entry.Id})");
                context.Output.Detail($"  channel:      {version.Channel}");
                context.Output.Detail($"  commit:       {version.CommitHash}");
                context.Output.Detail($"  distribution: {install.Distribution}");
                context.Output.Detail($"  directory:    {install.InstallDir}");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
