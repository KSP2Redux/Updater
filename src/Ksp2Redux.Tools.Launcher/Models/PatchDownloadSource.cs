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

/// <summary>
/// Provides mirror-selection operations for patch download sources.
/// </summary>
public static class PatchDownloadSourceExtensions
{
    /// <summary>
    /// Gets the other configured public mirror.
    /// </summary>
    /// <param name="source">The mirror that failed or is currently selected.</param>
    /// <returns>The alternate public mirror.</returns>
    public static PatchDownloadSource GetAlternate(this PatchDownloadSource source) => source switch
    {
        PatchDownloadSource.R2 => PatchDownloadSource.GitHub,
        PatchDownloadSource.GitHub => PatchDownloadSource.R2,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown patch download source.")
    };
}
