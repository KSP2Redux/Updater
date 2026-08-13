using System.Diagnostics;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Replaces the running CLI with the newest published build.
/// </summary>
public sealed class SelfUpdateCommand : ReduxCommand<SelfUpdateSettings>
{
    /// <summary>
    /// The suffix the old binary is parked under while the new one takes its place.
    /// </summary>
    public const string SUPERSEDED_SUFFIX = ".old";

    /// <inheritdoc />

    protected override bool NoticeApplies => false;



    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        SelfUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        var executable = context.EnvironmentProvider.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return context.Output.Fail(
                ExitCode.SELF_UPDATE_FAILED,
                "Could not work out which file is running, so there is nothing to replace.");
        }

        RemoveSupersededBinary(context, executable);

        var releases = context.CreateReleaseService();
        CliRelease? latest;
        try
        {
            latest = await context.Output.StatusAsync(
                "Checking for a newer release",
                _ => releases.FindLatestAsync(cancellationToken));
        }
        catch (Exception e)
        {
            return context.Output.Fail(ExitCode.SELF_UPDATE_FAILED, $"Could not reach GitHub: {e.Message}");
        }

        if (latest is null)
        {
            context.Output.Heading($"No release with a CLI build was found. Looking for tags starting {CliReleaseService.TAG_PREFIX}.");
            context.Output.Payload(
                new { ok = true, updated = false, current = context.RunningVersion.ToString(), latest = (string?)null },
                () => context.Output.Result(context.RunningVersion.ToString()));

            return ExitCode.SUCCESS;
        }

        var current = context.RunningVersion;
        if (latest.Version <= current)
        {
            context.Output.Heading($"Already on {current}, which is the newest published build.");
            context.Output.Payload(
                new { ok = true, updated = false, current = current.ToString(), latest = latest.Version.ToString() },
                () => context.Output.Result(current.ToString()));

            return ExitCode.SUCCESS;
        }

        if (settings.CheckOnly)
        {
            context.Output.Heading($"{latest.Version} is available. Run without --check to install it.");
            context.Output.Payload(
                new { ok = true, updateAvailable = true, current = current.ToString(), latest = latest.Version.ToString() },
                () => context.Output.Result(latest.Version.ToString()));

            return ExitCode.SUCCESS;
        }

        var answer = CliConfirm.Ask(
            context.Output,
            settings.AssumeYes,
            $"Replace {executable} with version {latest.Version}?",
            requireAnswer: true);

        switch (answer)
        {
            case ConfirmAnswer.Declined:
                return context.Output.Fail(ExitCode.CANCELLED, "Nothing was replaced.");
            case ConfirmAnswer.NeedsFlag:
                return context.Output.Fail(
                    ExitCode.USAGE_ERROR,
                    "Refusing to replace the running binary without a terminal to confirm on. Pass --yes.");
            case ConfirmAnswer.Approved:
            default:
                break;
        }

        try
        {
            var bytes = await context.Output.StatusAsync(
                $"Downloading {latest.AssetName}",
                _ => releases.DownloadAsync(latest, cancellationToken));

            await ReplaceAsync(context, executable, bytes);
        }
        catch (OperationCanceledException)
        {
            return context.Output.Fail(ExitCode.CANCELLED, "Update cancelled.");
        }
        catch (Exception e)
        {
            return context.Output.Fail(ExitCode.SELF_UPDATE_FAILED, e.Message);
        }

        context.Output.Heading($"Updated to {latest.Version}.");
        context.Output.Payload(
            new
            {
                ok = true,
                updated = true,
                current = latest.Version.ToString(),
                previous = current.ToString(),
                executable,
            },
            () => context.Output.Result(latest.Version.ToString()));

        return ExitCode.SUCCESS;
    }

    // Windows will not let a running executable be overwritten, but it will let it be renamed, so
    // the old build is parked beside the new one and cleaned up the next time this runs. On Linux
    // the rename replaces the directory entry while the running process keeps its open inode.
    private static async Task ReplaceAsync(CliContext context, string executable, byte[] bytes)
    {
        var fileSystem = context.FileSystem;
        var staging = executable + ".new";

        await fileSystem.File.WriteAllBytesAsync(staging, bytes);

        if (context.OperatingSystemService.IsLinux())
        {
            await MarkExecutableAsync(staging);
            fileSystem.File.Move(staging, executable, overwrite: true);
            return;
        }

        var superseded = executable + SUPERSEDED_SUFFIX;
        if (fileSystem.File.Exists(superseded))
        {
            fileSystem.File.Delete(superseded);
        }

        fileSystem.File.Move(executable, superseded);

        try
        {
            fileSystem.File.Move(staging, executable);
        }
        catch (Exception)
        {
            fileSystem.File.Move(superseded, executable);
            throw;
        }
    }

    // Shelling out to chmod rather than setting the mode through the file system abstraction, which
    // the platform analyzer will not accept behind a runtime check it cannot see through. The
    // launcher's own updater does the same thing for the same reason.
    private static async Task MarkExecutableAsync(string path)
    {
        using var chmod = Process.Start("chmod", $"+x \"{path}\"")
                          ?? throw new InvalidOperationException($"Could not start chmod for {path}.");

        await chmod.WaitForExitAsync();

        if (chmod.ExitCode != 0)
        {
            throw new InvalidOperationException($"chmod +x on {path} exited with code {chmod.ExitCode}.");
        }
    }

    private static void RemoveSupersededBinary(CliContext context, string executable)
    {
        var superseded = executable + SUPERSEDED_SUFFIX;
        try
        {
            if (context.FileSystem.File.Exists(superseded))
            {
                context.FileSystem.File.Delete(superseded);
            }
        }
        catch (Exception e)
        {
            context.LogService.Warn($"Could not delete the superseded binary at {superseded}: {e.Message}");
        }
    }
}
