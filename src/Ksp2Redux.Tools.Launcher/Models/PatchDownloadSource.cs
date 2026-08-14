namespace Ksp2Redux.Tools.Launcher.Models;

/// <summary>
/// The selected public patch-distribution provider.
/// </summary>
public enum PatchDownloadSource
{
    /// <summary>
    /// Download through the KSP2 Redux Cloudflare R2 domain.
    /// </summary>
    R2,

    /// <summary>
    /// Download from the GitHub release backup.
    /// </summary>
    GitHub
}
