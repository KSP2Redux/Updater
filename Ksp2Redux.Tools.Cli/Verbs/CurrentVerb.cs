using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Cli.Verbs;

/// <summary>
/// Reports the version currently installed into a KSP2 install.
/// </summary>
public static class CurrentVerb
{
    /// <summary>
    /// Prints the version detected in the install's own assembly.
    /// </summary>
    /// <param name="context">The shared verb context.</param>
    /// <param name="options">The parsed options for this verb.</param>
    /// <returns>One of the values on <see cref="ExitCode" />.</returns>
    // The version comes from reading the install's Assembly-CSharp rather than from the launcher
    // config, so it stays correct after a patch applied by something other than the launcher.
    public static Task<int> RunAsync(CliContext context, CurrentOptions options)
    {
        var entry = context.ResolveInstallEntry(options.Install);
        if (entry is null)
        {
            return Task.FromResult(context.FailInstallNotFound(options.Install));
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
                context.Output.Progress($"  install:      {entry.Name} ({entry.Id})");
                context.Output.Progress($"  channel:      {version.Channel}");
                context.Output.Progress($"  commit:       {version.CommitHash}");
                context.Output.Progress($"  distribution: {install.Distribution}");
                context.Output.Progress($"  directory:    {install.InstallDir}");
            });

        return Task.FromResult(ExitCode.SUCCESS);
    }
}
