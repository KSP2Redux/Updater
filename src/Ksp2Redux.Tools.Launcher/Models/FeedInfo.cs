using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Common.Models;

namespace Ksp2Redux.Tools.Launcher.Models;

public class FeedInfo
{
    public string Repository { get; set; } = string.Empty;
    public string? Token { get; set; }

    /// <summary>
    /// Gets or sets an environment-variable reference used instead of saving a token in the configuration.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenEnvironmentVariable { get; set; }
    public string Filename { get; set; } = string.Empty;
    public string? R2ManifestUrl { get; set; }
    public string? GitHubManifestUrl { get; set; }

    /// <summary>
    /// Copy the configured authority locations without retaining credentials in an operation selection.
    /// </summary>
    /// <returns>The nonsecret authority snapshot requiring a fresh configured feed lookup.</returns>
    public FeedInfo SnapshotAuthority() => new()
    {
        Repository = Repository, Filename = ReleaseManifestValidator.InternalRecipeFeedPath(Filename),
        R2ManifestUrl = RecipeUrl(R2ManifestUrl), GitHubManifestUrl = RecipeUrl(GitHubManifestUrl)
    };

    /// <summary>
    /// Compare configured authority locations independently of current credentials.
    /// </summary>
    /// <param name="other">The selected nonsecret authority snapshot.</param>
    /// <returns>True if every configured authority location matches, false otherwise.</returns>
    public bool HasSameAuthority(FeedInfo other)
    {
        FeedInfo current = SnapshotAuthority();
        FeedInfo selected = other.SnapshotAuthority();
        return current.Repository == selected.Repository && current.Filename == selected.Filename &&
            current.R2ManifestUrl == selected.R2ManifestUrl && current.GitHubManifestUrl == selected.GitHubManifestUrl;
    }

    /// <summary>
    /// Resolve the branch's recipe authority and current credentials without changing the stored configuration.
    /// </summary>
    /// <param name="environmentProvider">The current process environment.</param>
    /// <returns>A transient transport configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured token environment variable is empty.</exception>
    public FeedInfo ForRecipeFeed(IEnvironmentProvider environmentProvider)
    {
        FeedInfo result = SnapshotAuthority();
        result.Token = string.IsNullOrWhiteSpace(TokenEnvironmentVariable) ? Token
            : environmentProvider.GetEnvironmentVariable(TokenEnvironmentVariable)
                ?? throw new InvalidOperationException($"Token environment variable '{TokenEnvironmentVariable}' is not set.");
        if (!string.IsNullOrWhiteSpace(TokenEnvironmentVariable) && string.IsNullOrWhiteSpace(result.Token))
            throw new InvalidOperationException($"Token environment variable '{TokenEnvironmentVariable}' is empty.");
        return result;
    }

    private static string? RecipeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        string path = ReleaseManifestValidator.InternalRecipeFeedPath(uri.AbsolutePath);
        return path == uri.AbsolutePath ? url : new UriBuilder(uri) { Path = path }.Uri.AbsoluteUri;
    }

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
