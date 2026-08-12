using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// A CLI release on GitHub.
/// </summary>
/// <param name="Version">The version the tag names.</param>
/// <param name="AssetName">The asset built for this platform.</param>
/// <param name="DownloadUrl">Where that asset can be fetched from.</param>
/// <param name="Sha256">The digest GitHub published for it, lower case hex.</param>
/// <param name="Notes">The release body, or null when it has none.</param>
public sealed record CliRelease(Version Version, string AssetName, string DownloadUrl, string Sha256, string? Notes);

/// <summary>
/// Finds and fetches the CLI's own releases.
/// </summary>
// The launcher ships from tags starting updater-v and picks its asset by looking for "win" or
// "linux" in the name, so the CLI ships from its own cli-v tags. A launcher already installed
// somewhere would otherwise see these binaries as an update to itself.
public sealed class CliReleaseService
{
    /// <summary>
    /// The tag prefix that marks a release as the CLI's.
    /// </summary>
    public const string TAG_PREFIX = "cli-v";

    private const string SHA256_PREFIX = "sha256:";
    private const string WINDOWS_ASSET = "redux-launcher-cli-win-x64.exe";
    private const string LINUX_ASSET = "redux-launcher-cli-linux-x64";

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly bool _isLinux;

    /// <summary>
    /// Initializes the service against the repository the launcher config points at.
    /// </summary>
    /// <param name="repositoryUrl">The GitHub repository holding the releases.</param>
    /// <param name="isLinux">True when the running platform wants the linux asset.</param>
    /// <param name="version">The running version, sent as the user agent.</param>
    /// <param name="timeout">How long to wait on GitHub, or null for a minute.</param>
    /// <param name="handler">The transport to use, or null for a plain one. Supplied by tests.</param>
    public CliReleaseService(
        string repositoryUrl,
        bool isLinux,
        string version,
        TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        _isLinux = isLinux;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(new ProductHeaderValue("redux-launcher-cli", version)));

        var uri = new Uri(repositoryUrl.TrimEnd('/'));
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _owner = parts[0];
        _repo = parts[1];
    }

    /// <summary>
    /// Gets the asset name this platform installs.
    /// </summary>
    public string AssetName => _isLinux ? LINUX_ASSET : WINDOWS_ASSET;

    /// <summary>
    /// Finds the newest published CLI release that carries an asset for this platform.
    /// </summary>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>The release, or null when there is nothing installable.</returns>
    // A release whose assets are still uploading has no digest yet. The launcher learned this the
    // hard way, so an incomplete release is treated as one that does not exist.
    public async Task<CliRelease?> FindLatestAsync(CancellationToken cancellationToken)
    {
        var releases = await _http.GetFromJsonAsync<GitHubRelease[]>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases",
            cancellationToken) ?? [];

        return releases
            .Where(release => release.TagName.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase) && !release.Prerelease)
            .Select(release => new
            {
                Release = release,
                Parsed = Version.TryParse(release.TagName[TAG_PREFIX.Length..], out var version) ? version : null,
            })
            .Where(candidate => candidate.Parsed is not null)
            .OrderByDescending(candidate => candidate.Parsed)
            .Select(candidate => Describe(candidate.Release, candidate.Parsed!))
            .FirstOrDefault(release => release is not null);
    }

    /// <summary>
    /// Downloads an asset and checks it against the digest GitHub published.
    /// </summary>
    /// <param name="release">The release to download.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>The asset bytes.</returns>
    /// <exception cref="InvalidOperationException">The download did not match its digest.</exception>
    public async Task<byte[]> DownloadAsync(CliRelease release, CancellationToken cancellationToken)
    {
        var bytes = await _http.GetByteArrayAsync(release.DownloadUrl, cancellationToken);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{release.AssetName} did not match the checksum GitHub published for it. Expected {release.Sha256}, got {actual}.");
        }

        return bytes;
    }

    private CliRelease? Describe(GitHubRelease release, Version version)
    {
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset?.Digest?.StartsWith(SHA256_PREFIX, StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        return new CliRelease(
            version,
            asset.Name,
            asset.BrowserDownloadUrl,
            asset.Digest[SHA256_PREFIX.Length..].Trim().ToLowerInvariant(),
            release.Body);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";

        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

        [JsonPropertyName("assets")] public GitHubAsset[] Assets { get; set; } = [];

        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";

        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
