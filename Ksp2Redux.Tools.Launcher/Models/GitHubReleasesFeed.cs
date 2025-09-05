using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Models;

public class GitHubReleasesFeed
{
    private readonly string backingFilePath;
    private readonly string downloadStorageDir;
    private readonly HttpClient apiClient;

    private ReleaseInfo[] allReleases;

    public GitHubReleasesFeed(string backingFilePath, string githubRelativeRepoUri, string personalAccessToken, string downloadStorageDir)
    {
        this.backingFilePath = backingFilePath;
        this.downloadStorageDir = downloadStorageDir;
        apiClient = new()
        {
            BaseAddress = new Uri("https://api.github.com/repos/" + githubRelativeRepoUri + "/"),
        };
        // Need a UserAgent header, or API will reject the request with a 403.
        ProductHeaderValue header = new("Ksp2ReduxLauncher", Assembly.GetExecutingAssembly().GetName().Version?.ToString());
        ProductInfoHeaderValue userAgent = new(header);
        apiClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
        apiClient.DefaultRequestHeaders.Accept.Add(new("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(personalAccessToken))
        {
            apiClient.DefaultRequestHeaders.Authorization = new("Bearer", personalAccessToken);
        }

        allReleases = [];
    }

    public class ReleaseInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("assets")] public ReleaseAssetsInfo[]? Assets { get; set; }
        [JsonPropertyName("body")] public required string Body { get; set; }
        [JsonPropertyName("tag_name")] public required string TagName { get; set; }
        [JsonPropertyName("prerelease")] public bool IsPrerelease { get; set; }

        public override string ToString()
        {
            return $"tag:{TagName} id:{Id} prerelease:{IsPrerelease}";
        }


        public GameVersion ParseVersion()
        {
            var tokens = TagName.Split('.');
            // remove optional leading 'v' from version
            if (tokens[0][0] == 'v')
            {
                tokens[0] = tokens[0][1..];
            }
            Version version;
            string buildNumber;
            if (tokens.Length > 4)
            {
                version = new Version(string.Join('.', tokens[0..4]));
                buildNumber = tokens[4];
            }
            else
            {
                version = new Version(string.Join('.', tokens));
                buildNumber = "0";
            }

            return new GameVersion()
            {
                VersionNumber = version,
                BuildNumber = buildNumber,
                Channel = IsPrerelease ? ReleaseChannel.Beta : ReleaseChannel.Stable,
            };
        }
    }

    public class ReleaseAssetsInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }

        public override string? ToString()
        {
            return Name;
        }
    }

    // async?
    public void Initialize()
    {
        if (File.Exists(backingFilePath))
        {
            using var file = File.OpenRead(backingFilePath);
            allReleases = JsonSerializer.Deserialize<ReleaseInfo[]>(file) ?? [];
        }
    }

    public IEnumerable<GameVersion> GetAllVersions()
    {
        foreach (var release in allReleases!)
        {
            yield return release.ParseVersion();
        }
    }

    public async Task UpdateFromApi()
    {
        using var response = await apiClient.GetAsync("releases");
        var text = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
        response.EnsureSuccessStatusCode();
        //.WriteRequestToConsole();

        var jsonResponse = await response.Content.ReadAsStringAsync();

        allReleases = JsonSerializer.Deserialize<ReleaseInfo[]>(jsonResponse) ?? [];
        File.WriteAllText(backingFilePath, jsonResponse);
    }

    /// <summary>
    /// Download the .patch file from github.
    /// </summary>
    public async Task<string> DownloadPatch(GameVersion requestedVersion, bool forSteam, Action<string> log, Action<long, long> reportDownloadProgress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Find the download URL and file name for the requested version.
        ReleaseAssetsInfo? assetInfo = GetDownloadInfoForVersion(requestedVersion, forSteam);
        if (assetInfo is null || string.IsNullOrWhiteSpace(assetInfo.Name))
        {
            throw new FileNotFoundException($"Can't find patch for version {requestedVersion}");
        }
        string patchDownloadTo = Path.Combine(downloadStorageDir, assetInfo.Name);

        log($"Downloading {assetInfo.Name}");
        reportDownloadProgress(0, assetInfo.Size);

        // check if already downloaded
        if (!File.Exists(patchDownloadTo) || new FileInfo(patchDownloadTo).Length != assetInfo.Size)
        {
            // Download streaming directly to file
            using HttpRequestMessage request = new(HttpMethod.Get, $"releases/assets/{assetInfo.Id}");
            request.Headers.Accept.Add(new("application/octet-stream"));

            using var response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // need a way to monitor and report bytes transferred.
            using var downloadStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(patchDownloadTo, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192, useAsync: true);

            //await downloadStream.CopyToAsync(fileStream, ct);
            long totalBytesRead = 0;
            int bytesRead = 0;
            long contentLength = response.Content.Headers.ContentLength!.Value;
            var buffer = new byte[64 * 1024];
            while ((bytesRead = await downloadStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, ct);
                totalBytesRead += bytesRead;

                // Report progress
                reportDownloadProgress(totalBytesRead, contentLength);
            }
        }

        log("Download complete.");

        // return the path it was saved to.
        return patchDownloadTo;
    }

    private ReleaseAssetsInfo? GetDownloadInfoForVersion(GameVersion requestedVersion, bool forSteam)
    {
        foreach (var release in allReleases!)
        {
            if ((requestedVersion.Channel == ReleaseChannel.Stable && release.IsPrerelease) || (requestedVersion.Channel == ReleaseChannel.Beta && !release.IsPrerelease))
            {
                continue;
            }

            var releaseVersion = release.ParseVersion();

            if (releaseVersion.Equals(requestedVersion))
            {
                // We found the release.  Find the right asset file.
                foreach (var asset in release.Assets!)
                {
                    var name = asset.Name;
                    if (string.IsNullOrEmpty(name) || !name.EndsWith(".patch")) continue;

                    if ((forSteam && name.Contains("steam")) || (!forSteam && name.Contains("portable")))
                    {
                        return asset;
                    }
                }
            }
        }
        return null;
    }
}
