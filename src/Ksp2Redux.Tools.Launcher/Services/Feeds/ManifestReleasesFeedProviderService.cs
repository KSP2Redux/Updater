using System.Net.Http.Headers;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Models;
using Octokit;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

public interface IManifestReleasesFeedProviderService
{
    Task<ReleaseManifest?> GetManifest(FeedInfo feed);
    Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ReleasePatch patch, CancellationToken ct);
}

public class ManifestReleasesFeedProviderService(IAssemblyService assemblyService, ILogService log) : IManifestReleasesFeedProviderService
{
    private readonly Dictionary<FeedInfo, GitHubClient> _clients = new();
    // ResponseHeadersRead means this only bounds getting the response headers for a patch download,
    // not the body copy that follows - safe to keep short even for large patch files.
    private readonly HttpClient _downloadClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private GitHubClient GetOrCreateClient(FeedInfo feed)
    {
        if (_clients.TryGetValue(feed, out var existing))
            return existing;

        var header = new Octokit.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString());
        var client = new GitHubClient(header);
        if (!string.IsNullOrWhiteSpace(feed.Token))
        {
            client.Credentials = new Credentials(feed.Token);
        }

        _clients.Add(feed, client);
        return client;
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
        var (owner, name) = ParseRepository(feed.Repository);

        if (!string.IsNullOrWhiteSpace(feed.Token))
        {
            log.Info($"Fetching manifest via authenticated GitHub API: {owner}/{name}/{feed.Filename}@main");
            try
            {
                var bytes = await GetOrCreateClient(feed).Repository.Content
                    .GetRawContentByRef(owner, name, feed.Filename, "main");
                log.Info($"Authenticated manifest fetch returned {bytes.Length} bytes for {owner}/{name}/{feed.Filename}.");
                var manifest = System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(bytes);
                if (manifest == null)
                {
                    log.Warn($"Authenticated manifest at {owner}/{name}/{feed.Filename} deserialized to null.");
                }
                return manifest;
            }
            catch (Exception ex)
            {
                log.Error($"Authenticated manifest fetch failed for {owner}/{name}/{feed.Filename}.", ex);
                throw;
            }
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
            return manifest;
        }
        catch (Exception ex)
        {
            log.Error($"Manifest fetch failed for {rawUrl}.", ex);
            throw;
        }
    }

    public async Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ReleasePatch patch, CancellationToken ct)
    {
        var url = patch.Url;
        var hasToken = !string.IsNullOrWhiteSpace(feed.Token);

        if (hasToken && TryParseBrowserDownloadUrl(patch.Url, out var parsed))
        {
            log.Info($"Resolving authenticated asset URL via Octokit for {parsed.Owner}/{parsed.Repo} tag={parsed.Tag} name={parsed.Name}.");
            try
            {
                var release = await GetOrCreateClient(feed).Repository.Release
                    .Get(parsed.Owner, parsed.Repo, parsed.Tag);
                var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, parsed.Name, StringComparison.Ordinal));
                if (asset != null) url = asset.Url;
                else log.Warn($"No matching asset '{parsed.Name}' found in release {parsed.Owner}/{parsed.Repo}@{parsed.Tag}. Falling back to public download URL.");
            }
            catch (Exception ex)
            {
                log.Error($"Authenticated asset URL resolution failed for {parsed.Owner}/{parsed.Repo}@{parsed.Tag}. Falling back to public download URL.", ex);
            }
        }

        log.Info($"Downloading patch v{patch.Version} ({patch.Size} bytes) from {url}.");

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            new System.Net.Http.Headers.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString())));
        if (hasToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", feed.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        try
        {
            var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            log.Info($"Patch download {url} -> HTTP {(int)response.StatusCode} {response.StatusCode}, ContentLength={response.Content.Headers.ContentLength}.");
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error($"Patch download failed for {url}.", ex);
            throw;
        }
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
