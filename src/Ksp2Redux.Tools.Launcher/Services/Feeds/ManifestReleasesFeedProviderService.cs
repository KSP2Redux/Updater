using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

public interface IManifestReleasesFeedProviderService
{
    /// <summary>
    /// Fetch fresh metadata from the configured online authority without a cached-authority fallback.
    /// </summary>
    /// <param name="feed">The explicitly configured authority and optional GitHub credentials.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The validated manifest and nonsecret online authority evidence.</returns>
    Task<TrustedReleaseManifest> GetTrustedManifestAsync(FeedInfo feed, CancellationToken ct) =>
        throw new NotSupportedException("This feed provider does not establish online release authority.");
    Task<ReleaseManifest?> GetManifest(FeedInfo feed);
    Task<HttpResponseMessage> DownloadFileAsync(FeedInfo feed, string url, long offset, CancellationToken ct);
}

/// <summary>
/// The existing configured HTTP and GitHub release-feed transport.
/// </summary>
/// <param name="assemblyService">The launcher identity for request headers.</param>
/// <param name="launcherConfigService">The configured feed source selection.</param>
/// <param name="log">The launcher log.</param>
/// <param name="httpHandler">An explicitly injected HTTP handler, or null for the production HTTPS handler.</param>
/// <param name="environmentProvider">The process environment, or null for the native environment.</param>
public class ManifestReleasesFeedProviderService(
    IAssemblyService assemblyService,
    ILauncherConfigService launcherConfigService,
    ILogService log,
    HttpMessageHandler? httpHandler = null,
    IEnvironmentProvider? environmentProvider = null) : IManifestReleasesFeedProviderService
{
    // ResponseHeadersRead means this only bounds getting the response headers for a patch download,
    // not the body copy that follows - safe to keep short even for large patch files.
    private readonly HttpClient _downloadClient = httpHandler is null
        ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
        : new HttpClient(httpHandler) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IEnvironmentProvider _environmentProvider = environmentProvider ?? SystemEnvironmentProvider.Instance;
    private const int ASSET_PAGE_SIZE = 100;
    private const int MAX_RELEASE_ASSETS = 1000;
    // The directory only resolves download URLs. Release authority and byte checks remain separate.
    private readonly SemaphoreSlim _assetDirectoryGate = new(1, 1);
    private AssetDirectory? _assetDirectory;

    private sealed record AssetDirectory(string TagUrl, string CredentialHash, DateTimeOffset Expires,
        Dictionary<string, string> Assets);

    /// <inheritdoc />
    public async Task<TrustedReleaseManifest> GetTrustedManifestAsync(FeedInfo feed, CancellationToken ct)
    {
        feed = feed.ForRecipeFeed(_environmentProvider);
        string? url = feed.GetManifestUrl(launcherConfigService.Config.PatchDownloadSource);
        bool githubApi = false;
        if (string.IsNullOrWhiteSpace(url))
        {
            var (owner, name) = ParseRepository(feed.Repository);
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name) ||
                owner.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
                name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) ||
                !Uri.TryCreate(feed.Repository, UriKind.Absolute, out var repository) ||
                repository.Scheme != "https" || repository.Host != "github.com" || repository.UserInfo.Length != 0)
                throw new InvalidDataException("Full-source repository authority must be a configured HTTPS GitHub repository.");
            githubApi = !string.IsNullOrWhiteSpace(feed.Token);
            if (feed.Filename.Split('/') is { Length: > 32 } || feed.Filename.Split('/').Any(segment =>
                    string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.Contains('\\')))
                throw new InvalidDataException("Configured release metadata filename contains invalid repository path segments.");
            string filename = string.Join('/', feed.Filename.Split('/').Select(Uri.EscapeDataString));
            url = githubApi ? $"https://api.github.com/repos/{owner}/{name}/contents/{filename}?ref=main"
                : $"https://raw.githubusercontent.com/{owner}/{name}/main/{filename}";
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var authority) || authority.Scheme != "https" ||
            authority.UserInfo.Length != 0 || authority.Fragment.Length != 0)
            throw new InvalidDataException("Full-source metadata requires a configured HTTPS authority without embedded credentials.");
        using var request = CreateRequest(HttpMethod.Get, url);
        if (githubApi)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", feed.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        }
        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != "https")
            throw new InvalidDataException("The configured metadata authority redirected outside HTTPS.");
        byte[] json = await ReadMetadataAsync(response, ct);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json)
            ?? throw new InvalidDataException("The online release feed is null.");
        ReleaseManifestValidator.Validate(manifest);
        // HTTPS authenticates the configured online publisher. No offline signature is implied.
        string evidence = $"online-https:{authority.GetLeftPart(UriPartial.Path)}";
        return new TrustedReleaseManifest(manifest, evidence);
    }

    private static async Task<byte[]> ReadMetadataAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > ReleaseManifestValidator.MAX_ONLINE_FEED_BYTES)
            throw new InvalidDataException("Release feed exceeds the 64 MiB metadata budget.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > ReleaseManifestValidator.MAX_ONLINE_FEED_BYTES)
                throw new InvalidDataException("Release feed exceeds the 64 MiB metadata budget.");
            bytes.Write(buffer, 0, read);
        }
        byte[] json = bytes.ToArray();
        using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }))
        {
            RejectDuplicates(document.RootElement);
        }
        return json;
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The release feed repeats a JSON property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                RejectDuplicates(element);
            }
        }
    }

    private static (string Owner, string Name) ParseRepository(string repository)
    {
        var trimmed = repository.TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return (segments[0], segments[1]);
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts[1]);
    }

    public async Task<ReleaseManifest?> GetManifest(FeedInfo feed)
    {
        feed = feed.ForRecipeFeed(_environmentProvider);
        string? directUrl = feed.GetManifestUrl(launcherConfigService.Config.PatchDownloadSource);
        if (!string.IsNullOrWhiteSpace(directUrl))
            return await GetPublicManifest(directUrl, launcherConfigService.Config.PatchDownloadSource);

        var (owner, name) = ParseRepository(feed.Repository);

        if (!string.IsNullOrWhiteSpace(feed.Token))
        {
            return (await GetTrustedManifestAsync(feed, CancellationToken.None)).Manifest;
        }

        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{name}/main/{feed.Filename}";
        log.Info($"Fetching manifest via raw URL: {rawUrl}");
        var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            new System.Net.Http.Headers.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString())));
        try
        {
            using var response = await _downloadClient.SendAsync(request);
            log.Info($"Manifest fetch {rawUrl} -> HTTP {(int)response.StatusCode} {response.StatusCode}, ContentLength={response.Content.Headers.ContentLength}.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<ReleaseManifest>(stream);
            if (manifest == null)
            {
                log.Warn($"Manifest at {rawUrl} deserialized to null.");
            }
            else
            {
                ReleaseManifestValidator.Validate(manifest);
            }
            return manifest;
        }
        catch (Exception ex)
        {
            log.Error($"Manifest fetch failed for {rawUrl}.", ex);
            throw;
        }
    }

    private async Task<ReleaseManifest?> GetPublicManifest(string url, PatchDownloadSource source)
    {
        var ct = CancellationToken.None;
        log.Info($"Fetching {source} manifest: {url}");
        var request = CreateRequest(HttpMethod.Get, url);
        try
        {
            using var response = await _downloadClient.SendAsync(request, ct);
            log.Info($"Manifest fetch {url} -> HTTP {(int)response.StatusCode} {response.StatusCode}, ContentLength={response.Content.Headers.ContentLength}.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<ReleaseManifest>(stream, cancellationToken: ct);
            if (manifest is null)
                throw new InvalidDataException($"Manifest at {url} deserialized to null.");

            ReleaseManifestValidator.Validate(manifest);
            return manifest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error($"Manifest fetch failed for {url}.", ex);
            throw new PatchDownloadException(source, $"Could not load the {source} release manifest.", ex);
        }
    }

    public async Task<HttpResponseMessage> DownloadFileAsync(FeedInfo feed, string url, long offset, CancellationToken ct)
    {
        feed = feed.ForRecipeFeed(_environmentProvider);
        var hasToken = !string.IsNullOrWhiteSpace(feed.Token);

        if (hasToken && TryParseBrowserDownloadUrl(url, out var parsed) && IsConfiguredRepository(feed, parsed.Owner, parsed.Repo))
        {
            log.Info($"Resolving authenticated asset URL for {parsed.Owner}/{parsed.Repo} tag={parsed.Tag} name={parsed.Name}.");
            try
            {
                string? asset = await ResolveAssetAsync(feed, parsed, ct);
                if (asset != null) url = asset;
                else log.Warn($"No matching asset '{parsed.Name}' found in release {parsed.Owner}/{parsed.Repo}@{parsed.Tag}. Falling back to public download URL.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidDataException)
            {
                log.Error($"Authenticated asset URL resolution failed for {parsed.Owner}/{parsed.Repo}@{parsed.Tag}. Falling back to public download URL.", ex);
            }
        }

        log.Info(offset > 0 ? $"Resuming download from byte {offset}: {url}" : $"Downloading {url}.");

        var request = CreateRequest(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }
        if (hasToken && IsRepositoryAssetUrl(feed, url))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", feed.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        try
        {
            var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (hasToken && IsRepositoryAssetUrl(feed, url) && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                InvalidateAssetDirectory(url);
            if (Uri.TryCreate(url, UriKind.Absolute, out var requested) && requested.Scheme == "https" &&
                response.RequestMessage?.RequestUri is { } final && final.Scheme != "https")
            {
                response.Dispose();
                throw new InvalidDataException("The HTTPS artifact transport redirected outside HTTPS.");
            }
            log.Info($"Patch download {url} -> HTTP {(int)response.StatusCode} {response.StatusCode}, ContentLength={response.Content.Headers.ContentLength}.");
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error($"Patch download failed for {url}.", ex);
            throw;
        }
    }

    private async Task<string?> ResolveAssetAsync(FeedInfo feed,
        (string Owner, string Repo, string Tag, string Name) asset, CancellationToken ct)
    {
        string releaseRoot = $"https://api.github.com/repos/{asset.Owner}/{asset.Repo}/releases";
        string tagUrl = $"{releaseRoot}/tags/{Uri.EscapeDataString(asset.Tag)}";
        string token = feed.Token!;
        string credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await _assetDirectoryGate.WaitAsync(ct);
        try
        {
            AssetDirectory? cached = Volatile.Read(ref _assetDirectory);
            if (cached is not null && cached.TagUrl == tagUrl && cached.CredentialHash == credentialHash &&
                cached.Expires > DateTimeOffset.UtcNow)
                return cached.Assets.GetValueOrDefault(asset.Name);

            using var release = JsonDocument.Parse(await ReadGitHubMetadataAsync(tagUrl, token, ct));
            var assets = new Dictionary<string, string>(StringComparer.Ordinal);
            if (release.RootElement.TryGetProperty("id", out var id))
            {
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long releaseId) || releaseId <= 0)
                    throw new InvalidDataException("GitHub returned an invalid release ID.");
                for (int page = 1; ; page++)
                {
                    string url = $"{releaseRoot}/{releaseId}/assets?per_page={ASSET_PAGE_SIZE}&page={page}";
                    using var document = JsonDocument.Parse(await ReadGitHubMetadataAsync(url, token, ct));
                    int count = document.RootElement.GetArrayLength();
                    if (count > ASSET_PAGE_SIZE)
                        throw new InvalidDataException("GitHub release asset page exceeded its requested size.");
                    AddAssets(feed, assets, document.RootElement);
                    if (count < ASSET_PAGE_SIZE)
                        break;
                }
            }
            else
            {
                AddAssets(feed, assets, release.RootElement.GetProperty("assets"));
            }

            Volatile.Write(ref _assetDirectory, new AssetDirectory(tagUrl, credentialHash, DateTimeOffset.UtcNow.AddMinutes(5), assets));
            return assets.GetValueOrDefault(asset.Name);
        }
        finally
        {
            _assetDirectoryGate.Release();
        }
    }

    private async Task<byte[]> ReadGitHubMetadataAsync(string url, string token, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && (final.Scheme != "https" || final.Host != "api.github.com"))
            throw new InvalidDataException("GitHub release metadata redirected outside its HTTPS API.");
        return await ReadMetadataAsync(response, ct);
    }

    private static void AddAssets(FeedInfo feed, Dictionary<string, string> assets, JsonElement page)
    {
        foreach (var candidate in page.EnumerateArray())
        {
            string? name = candidate.GetProperty("name").GetString();
            string? resolved = candidate.GetProperty("url").GetString();
            if (string.IsNullOrEmpty(name) || resolved is null || !IsRepositoryAssetUrl(feed, resolved) || !assets.TryAdd(name, resolved))
                throw new InvalidDataException("GitHub release metadata must identify unique assets in its configured repository.");
            if (assets.Count > MAX_RELEASE_ASSETS)
                throw new InvalidDataException("GitHub release asset list exceeds its record budget.");
        }
    }

    private void InvalidateAssetDirectory(string assetUrl)
    {
        AssetDirectory? cached = Volatile.Read(ref _assetDirectory);
        if (cached is not null && cached.Assets.ContainsValue(assetUrl))
            Interlocked.CompareExchange(ref _assetDirectory, null, cached);
    }

    private static bool IsRepositoryAssetUrl(FeedInfo feed, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "api.github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is
            ["repos", var owner, var repository, "releases", "assets", _] && IsConfiguredRepository(feed, owner, repository);

    private static bool IsConfiguredRepository(FeedInfo feed, string owner, string repository)
    {
        if (!Uri.TryCreate(feed.Repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || uri.UserInfo.Length != 0)
            return false;
        var configured = ParseRepository(feed.Repository);
        return configured.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
            configured.Name.Equals(repository, StringComparison.OrdinalIgnoreCase);
    }

    public Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ReleasePatch patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch.Url))
            throw new InvalidOperationException("The patch does not have a legacy download URL.");
        return DownloadFileAsync(feed, patch.Url, 0, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            new System.Net.Http.Headers.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString())));
        return request;
    }

    private static bool TryParseBrowserDownloadUrl(string url, out (string Owner, string Repo, string Tag, string Name) parsed)
    {
        parsed = default;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length < 6 || seg[2] != "releases" || seg[3] != "download") return false;
        var name = string.Join('/', seg.Skip(5));
        parsed = (seg[0], seg[1], Uri.UnescapeDataString(seg[4]), Uri.UnescapeDataString(name));
        return true;
    }
}
