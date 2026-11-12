namespace Ksp2Redux.Tools.Launcher.Models;

public class FeedInfo
{
    public string Repository { get; set; } = string.Empty;
    public string? Token { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string? R2ManifestUrl { get; set; }
    public string? GitHubManifestUrl { get; set; }

    /// <summary>
    /// Copy the configured authority locations without retaining credentials in an operation selection.
    /// </summary>
    /// <returns>The nonsecret authority snapshot requiring a fresh configured feed lookup.</returns>
    public FeedInfo SnapshotAuthority() => new()
    {
        Repository = Repository, Filename = Filename,
        R2ManifestUrl = R2ManifestUrl, GitHubManifestUrl = GitHubManifestUrl
    };

    /// <summary>
    /// Compare configured authority locations independently of current credentials.
    /// </summary>
    /// <param name="other">The selected nonsecret authority snapshot.</param>
    /// <returns>True if every configured authority location matches, false otherwise.</returns>
    public bool HasSameAuthority(FeedInfo other) => Repository == other.Repository && Filename == other.Filename &&
        R2ManifestUrl == other.R2ManifestUrl && GitHubManifestUrl == other.GitHubManifestUrl;

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
