using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Launcher.Models;
using Octokit;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IManifestReleasesFeedProviderService
{
    Task<ManifestReleasesFeed.Manifest?> GetManifest(FeedInfo feed);
    Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ManifestReleasesFeed.Patch patch, CancellationToken ct);
}

public class ManifestReleasesFeedProviderService(IAssemblyService assemblyService) : IManifestReleasesFeedProviderService
{
    private readonly Dictionary<FeedInfo, GitHubClient> _clients = new();
    private readonly HttpClient _downloadClient = new();

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

    public async Task<ManifestReleasesFeed.Manifest?> GetManifest(FeedInfo feed)
    {
        var (owner, name) = ParseRepository(feed.Repository);

        if (!string.IsNullOrWhiteSpace(feed.Token))
        {
            var bytes = await GetOrCreateClient(feed).Repository.Content
                .GetRawContentByRef(owner, name, feed.Filename, "main");
            return System.Text.Json.JsonSerializer.Deserialize<ManifestReleasesFeed.Manifest>(bytes);
        }

        var rawUrl = $"https://raw.githubusercontent.com/{owner}/{name}/main/{feed.Filename}";
        var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            new System.Net.Http.Headers.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString())));
        using var response = await _downloadClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await System.Text.Json.JsonSerializer.DeserializeAsync<ManifestReleasesFeed.Manifest>(stream);
    }

    public async Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ManifestReleasesFeed.Patch patch, CancellationToken ct)
    {
        var url = patch.url;
        var hasToken = !string.IsNullOrWhiteSpace(feed.Token);

        if (hasToken && TryParseBrowserDownloadUrl(patch.url, out var parsed))
        {
            var release = await GetOrCreateClient(feed).Repository.Release
                .Get(parsed.Owner, parsed.Repo, parsed.Tag);
            var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, parsed.Name, StringComparison.Ordinal));
            if (asset != null) url = asset.Url;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            new System.Net.Http.Headers.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString())));
        if (hasToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", feed.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return response;
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
