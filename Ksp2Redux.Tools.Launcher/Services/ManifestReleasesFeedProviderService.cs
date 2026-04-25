using System;
using System.Collections.Generic;
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
        var request = new HttpRequestMessage(HttpMethod.Get, patch.url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            new System.Net.Http.Headers.ProductHeaderValue("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString())));
        if (!string.IsNullOrWhiteSpace(feed.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", feed.Token);
        }

        var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }
}
