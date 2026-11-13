using Ksp2Redux.Tools.Launcher.Localization;

namespace Ksp2Redux.Tools.Launcher.Models;

public class InstallPlan
{
    /// <summary>Removes Redux by restoring the uninstall.zip snapshot.</summary>
    public sealed record UninstallStep;

    /// <summary>Restores the uninstall.zip snapshot so the next patch starts from stock.</summary>
    public sealed record RevertToStockStep;

    /// <summary>Applies the distribution's prepatch, snapshotting the install into uninstall.zip first if it has none.</summary>
    public sealed record PrepatchStep;

    /// <summary>Applies one patch file.</summary>
    /// <param name="ResolvePath">Returns the patch path from (log, download progress, token), or null when <paramref name="DownloadRequest"/> fetches it.</param>
    /// <param name="Description">The user-facing step description.</param>
    /// <param name="DeleteAfter">Deletes the resolved patch file once applied, for downloaded patches.</param>
    /// <param name="DownloadSize">The bytes the step downloads, or 0.</param>
    /// <param name="DownloadRequest">The remote patch the step downloads, if any.</param>
    public sealed record ApplyPatchStep(
        Func<Action<LogMessage>, Action<long, long>, CancellationToken, Task<string>>? ResolvePath,
        LogMessage Description,
        bool DeleteAfter = false,
        long DownloadSize = 0,
        PatchDownloadRequest? DownloadRequest = null);

    public union Step(UninstallStep, RevertToStockStep, PrepatchStep, ApplyPatchStep);

    public List<Step> Steps { get; init; } = [];

    public void Uninstall()
    {
        Steps.Insert(0, new UninstallStep());
    }

    public void RevertToStock()
    {
        Steps.Insert(0, new RevertToStockStep());
    }

    public void Prepatch()
    {
        Steps.Insert(0, new PrepatchStep());
    }

    public void ApplyPatchFile(string path)
    {
        Steps.Insert(0, new ApplyPatchStep((_, _, _) => Task.FromResult(path), LocalizationService.Default.Message("Plan.ApplyingPatchFile", path)));
    }

    public void ApplyPatchFile(Func<Action<LogMessage>, Action<long, long>, CancellationToken, Task<string>> path, LogMessage description, long downloadSize = 0)
    {
        Steps.Insert(0, new ApplyPatchStep(path, description, DeleteAfter: true, DownloadSize: downloadSize));
    }

    /// <summary>
    /// Adds a remotely downloaded patch application step.
    /// </summary>
    /// <param name="request">The patch download request.</param>
    /// <param name="description">The user-facing step description.</param>
    public void ApplyPatchFile(PatchDownloadRequest request, LogMessage description)
    {
        Steps.Insert(0, new ApplyPatchStep(null, description, DeleteAfter: true, DownloadSize: request.Patch.Size, DownloadRequest: request));
    }

    /// <summary>The remote patches the plan downloads, in step order.</summary>
    public IEnumerable<PatchDownloadRequest> DownloadRequests =>
        Steps.Select(step => step is ApplyPatchStep { DownloadRequest: { } request } ? request : null).OfType<PatchDownloadRequest>();

    // Cost is the total download size in bytes across all steps.
    public long Cost => Steps.Sum(step => step is ApplyPatchStep apply ? apply.DownloadSize : 0);

    public bool Patches => Steps.Any(step => step is PrepatchStep or ApplyPatchStep);

    public static InstallPlan operator +(InstallPlan a, InstallPlan b) => new() { Steps = a.Steps.Concat(b.Steps).ToList() };
}
