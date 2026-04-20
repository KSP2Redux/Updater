using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IManifestReleasesFeedProviderService
{
    Task<ManifestReleasesFeed.Manifest?> GetManifest(FeedInfo feed);
    Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ManifestReleasesFeed.Patch patch, CancellationToken ct);
}

public class ManifestReleasesFeedProviderService(IAssemblyService assemblyService) : IManifestReleasesFeedProviderService
{
    private readonly Dictionary<FeedInfo, HttpClient> apiClients = new();

    private HttpClient GetOrCreateClient(FeedInfo feed)
    {
        if (apiClients.TryGetValue(feed, out var existingClient))
            return existingClient;
        
        
        HttpClient newApiClient = new()
        {
            BaseAddress = new Uri("https://api.github.com/repos/" + feed.Repository + "/"),
        };
        ProductHeaderValue header = new("Ksp2ReduxLauncher", assemblyService.GetName().Version?.ToString());
        ProductInfoHeaderValue userAgent = new(header);
        newApiClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
        newApiClient.DefaultRequestHeaders.Accept.Add(new("application/vnd.github.v3.raw"));
        if (!string.IsNullOrWhiteSpace(feed.Token))
        {
            newApiClient.DefaultRequestHeaders.Authorization = new("Bearer", feed.Token);
        }
        
        apiClients.Add(feed, newApiClient);
        return newApiClient;
    }
    
    public async Task<ManifestReleasesFeed.Manifest?> GetManifest(FeedInfo feed)
    {
        var response = await GetOrCreateClient(feed).GetAsync(
            $"contents/{feed.Filename}?ref=main");
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        // Console.WriteLine($"Final url: {finalUrl}");
        response.EnsureSuccessStatusCode();
        ManifestReleasesFeed.Manifest? manifest = System.Text.Json.JsonSerializer.Deserialize<ManifestReleasesFeed.Manifest>(await response.Content.ReadAsStringAsync());
        return manifest;
    }

    public async Task<HttpResponseMessage> DownloadPatchAsync(FeedInfo feed, ManifestReleasesFeed.Patch patch, CancellationToken ct)
    {
        string assetApiUrl = await GetAssetApiUrl(feed, patch.url, ct);

        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, assetApiUrl);
        apiRequest.Headers.Accept.Add(new("application/octet-stream"));

        using var apiResponse = await GetOrCreateClient(feed)
            .SendAsync(apiRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        Uri downloadUri;

        if (apiResponse.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.MovedPermanently)
        {
            downloadUri = apiResponse.Headers.Location;
        }
        else if (apiResponse.IsSuccessStatusCode)
        {
            downloadUri = apiResponse.RequestMessage.RequestUri;
        }
        else
        {
            throw new Exception($"Failed to get download URL: {apiResponse.StatusCode}");
        }

        using var cleanClient = new HttpClient();

        var downloadResponse =
            await cleanClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
        downloadResponse.EnsureSuccessStatusCode();
        return downloadResponse;
    }
    
    private async Task<string> GetAssetApiUrl(FeedInfo feed, string browserUrl, CancellationToken ct)
    {
        var uri = new Uri(browserUrl);
        var segments = uri.Segments;

        string tag = Uri.UnescapeDataString(segments[^2].Trim('/'));
        string fileName = Uri.UnescapeDataString(segments[^1]);

        using var response = await GetOrCreateClient(feed).GetAsync($"releases/tags/{tag}", ct);
        response.EnsureSuccessStatusCode();

        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == fileName)
            {
                return asset.GetProperty("url").GetString();
            }
        }

        throw new FileNotFoundException($"Could not find asset '{fileName}' in release '{tag}'");
    }
}