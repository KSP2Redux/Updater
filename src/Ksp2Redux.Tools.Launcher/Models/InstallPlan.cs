using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Install;

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

    /// <summary>
    /// Gets the feed-selected full-source release to authenticate during execution.
    /// </summary>
    public string? FullSourceReleaseId { get; init; }

    /// <summary>
    /// Gets the explicitly selected full-source channel.
    /// </summary>
    public string? FullSourceChannel { get; init; }

    /// <summary>
    /// Gets the exact version selected from the feed.
    /// </summary>
    public string? FullSourceVersion { get; init; }

    /// <summary>
    /// Gets the nonsecret list-time authority selection requiring fresh configured metadata.
    /// </summary>
    public FeedInfo? FullSourceFeed { get; init; }

    /// <summary>
    /// Gets the selected complete target digest to compare during execution.
    /// </summary>
    public string? FullSourceManifestSha256 { get; init; }

    /// <summary>
    /// Gets the selected boot derivation permission to compare during execution.
    /// </summary>
    public bool? FullSourceAllowsDerivedBootConfig { get; init; }

    /// <summary>
    /// Gets the publisher-declared bounded artifact and pack download bytes.
    /// </summary>
    public long FullSourceDownloadBytes { get; init; }

    /// <summary>
    /// Gets whether a full-source generation is selected or already prepared.
    /// </summary>
    public bool IsFullSource => FullSourceReleaseId is not null || FullSourceTarget is not null;

    /// <summary>
    /// Gets the configured identity for an explicit full-source operation.
    /// </summary>
    public Guid? FullSourceInstallationId { get; init; }

    /// <summary>
    /// Gets the independently prepared release target for a full-source generation change.
    /// </summary>
    public Ksp2Redux.Tools.Launcher.Services.Install.FullSourcePreparedTarget? FullSourceTarget { get; init; }

    /// <summary>
    /// Gets independently verified original stock preparation for initial conversion.
    /// </summary>
    public Ksp2Redux.Tools.Launcher.Services.Install.FullSourceStockPreparation? FullSourceStock { get; init; }

    /// <summary>
    /// Gets whether this explicit full-source operation observes damaged owned files for repair.
    /// </summary>
    public bool IsFullSourceRepair { get; init; }

    internal void RequireFullSourceMatch(FullSourceReleaseIdentity identity, bool allowsDerivedBootConfig)
    {
        if (identity.Version != FullSourceVersion || identity.Channel != FullSourceChannel ||
            identity.ReleaseId != FullSourceReleaseId ||
            (FullSourceManifestSha256 is { } digest && !digest.Equals(identity.ManifestSha256, StringComparison.OrdinalIgnoreCase)) ||
            (FullSourceAllowsDerivedBootConfig is { } permission && permission != allowsDerivedBootConfig))
            throw new InvalidDataException("The selected release changed in the configured feed. Refresh the release list and select it again.");
    }

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
    public long Cost => FullSourceDownloadBytes + Steps.Sum(step => step is ApplyPatchStep apply ? apply.DownloadSize : 0);

    public bool Patches => Steps.Any(step => step is PrepatchStep or ApplyPatchStep);

    public static InstallPlan operator +(InstallPlan a, InstallPlan b) => new() { Steps = a.Steps.Concat(b.Steps).ToList() };
}
