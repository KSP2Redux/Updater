using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

/// <summary>
/// A download failure associated with a selected public distribution source.
/// </summary>
public sealed class PatchDownloadException : Exception
{
    /// <summary>
    /// Gets the source that failed.
    /// </summary>
    public PatchDownloadSource DownloadSource { get; }

    /// <summary>
    /// Creates a source-specific download failure.
    /// </summary>
    /// <param name="source">The source that failed.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PatchDownloadException(PatchDownloadSource source, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DownloadSource = source;
    }
}
