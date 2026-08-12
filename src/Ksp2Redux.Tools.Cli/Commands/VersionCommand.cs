using System.Reflection;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Reports the CLI's own version, where it is installed, and optionally what is published.
/// </summary>
public sealed class VersionCommand : ReduxCommand<VersionSettings>
{
    /// <inheritdoc />
    protected override bool NoticeApplies => false;

    /// <inheritdoc />
    // The first line stays the bare version string, because that is what the version verb printed
    // before this was a command of its own and what a script would be reading.
    protected override async Task<int> RunAsync(
        CliContext context,
        VersionSettings settings,
        CancellationToken cancellationToken)
    {
        var informational = typeof(VersionCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? context.RunningVersion.ToString();

        CliRelease? latest = null;
        string? checkFailure = null;

        if (settings.ShouldCheck)
        {
            try
            {
                latest = await context.Output.StatusAsync(
                    "Checking for a newer release",
                    _ => context.CreateReleaseService().FindLatestAsync(cancellationToken));
            }
            catch (Exception e)
            {
                checkFailure = e.Message;
            }
        }

        var updateAvailable = latest is not null && latest.Version > context.RunningVersion;

        context.Output.Payload(
            new
            {
                ok = true,
                version = informational,
                assemblyVersion = context.RunningVersion.ToString(),
                executable = context.EnvironmentProvider.ProcessPath,
                latest = latest?.Version.ToString(),
                updateAvailable = settings.ShouldCheck ? updateAvailable : (bool?)null,
                checkFailed = checkFailure,
            },
            () =>
            {
                context.Output.Result(informational);
                context.Output.Detail($"  executable: {context.EnvironmentProvider.ProcessPath}");

                if (!settings.ShouldCheck)
                {
                    return;
                }

                if (checkFailure is not null)
                {
                    context.Output.Warn($"Could not reach GitHub to check for a newer release: {checkFailure}");
                    return;
                }

                if (latest is null)
                {
                    context.Output.Detail("  latest:     no published release found");
                    return;
                }

                context.Output.Detail($"  latest:     {latest.Version}");
                if (updateAvailable)
                {
                    context.Output.Heading($"An update is available. Run 'redux-launcher-cli self-update' to install {latest.Version}.");
                }
            });

        return ExitCode.SUCCESS;
    }
}
