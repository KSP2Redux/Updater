namespace Ksp2Redux.Tools.Launcher.Models;

public class FeedInfo
{
    public string Repository { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string? R2ManifestUrl { get; set; }
    public string? GitHubManifestUrl { get; set; }

    /// <summary>
    /// Gets the direct public manifest URL for a source, if this is a public feed.
    /// </summary>
    /// <param name="source">The configured download source.</param>
    /// <returns>The direct URL, or null for a legacy repository feed.</returns>
    public string? GetManifestUrl(PatchDownloadSource source) => source switch
    {
        PatchDownloadSource.R2 => R2ManifestUrl,
        PatchDownloadSource.GitHub => GitHubManifestUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };
}