namespace Ksp2Redux.Tools.Launcher.Models;

public class InstallPlan
{
    // The Argument is string getPath(log, progress)
    // DeleteAfter indicates the resolved patch file should be deleted once applied (used for downloaded patches).
    // DownloadSize is the size in bytes of the file that will be fetched by Argument, or 0 if the step performs no download.
    public record struct Step(
        InstallPlanAction Action,
        Func<Action<string>, Action<long, long>, CancellationToken, Task<string>>? Argument = null,
        string ArgumentDescription = "Undescribed",
        bool DeleteAfter = false,
        long DownloadSize = 0,
        PatchDownloadRequest? DownloadRequest = null);

    public List<Step> Steps { get; init; } = [];

    public void Uninstall()
    {
        Steps.Insert(0,new Step(InstallPlanAction.Uninstall));
    }

    public void RevertToStock()
    {
        Steps.Insert(0,new Step(InstallPlanAction.RevertToStock));
    }

    public void Prepatch()
    {
        Steps.Insert(0,new Step(InstallPlanAction.Prepatch));
    }

    public void ApplyPatchFile(string path)
    {
        Steps.Insert(0,new Step(InstallPlanAction.ApplyPatchFile, (_,_,_) => Task.FromResult(path),$"Applying patch file at {path}"));
    }

    public void ApplyPatchFile(Func<Action<string>, Action<long, long>,CancellationToken,Task<string>> path, string description, long downloadSize = 0)
    {
        Steps.Insert(0, new Step(InstallPlanAction.ApplyPatchFile, path, description, DeleteAfter: true, DownloadSize: downloadSize));
    }

    /// <summary>
    /// Adds a remotely downloaded patch application step.
    /// </summary>
    /// <param name="request">The patch download request.</param>
    /// <param name="description">The user-facing step description.</param>
    public void ApplyPatchFile(PatchDownloadRequest request, string description)
    {
        Steps.Insert(0, new Step(
            InstallPlanAction.ApplyPatchFile,
            ArgumentDescription: description,
            DeleteAfter: true,
            DownloadSize: request.Patch.Size,
            DownloadRequest: request));
    }

    // Cost is the total download size in bytes across all steps.
    public long Cost => Steps.Sum(s => s.DownloadSize);

    public static InstallPlan operator +(InstallPlan a, InstallPlan b) => new() { Steps = a.Steps.Concat(b.Steps).ToList() };
}