using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Models;

public class GitHubReleasesFeed
{
    private readonly string backingFilePath;
    private readonly string githubRelativeRepoUri;
    private readonly HttpClient apiClient;

    private ReleaseInfo[] allReleases;

    public GitHubReleasesFeed(string backingFilePath, string githubRelativeRepoUri, string personalAccessToken)
    {
        this.backingFilePath = backingFilePath;
        this.githubRelativeRepoUri = githubRelativeRepoUri;
        apiClient = new()
        {
            BaseAddress = new Uri("https://api.github.com/repos/" + githubRelativeRepoUri + "/"),
        };
        // Need a UserAgent header, or API will reject the request with a 403.
        ProductHeaderValue header = new ProductHeaderValue("Ksp2ReduxLauncher", Assembly.GetExecutingAssembly().GetName().Version?.ToString());
        ProductInfoHeaderValue userAgent = new ProductInfoHeaderValue(header);
        apiClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
        apiClient.DefaultRequestHeaders.Accept.Add(new("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(personalAccessToken))
        {
            apiClient.DefaultRequestHeaders.Authorization = new("Bearer", personalAccessToken);
        }

        allReleases = Array.Empty<ReleaseInfo>();
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
    }

    public class ReleaseAssetsInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }

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
            var (version, buildNumber) = ParseVersionFromTag(release.TagName);
            yield return new GameVersion()
            {
                VersionNumber = version,
                BuildNumber = buildNumber,
                Channel = release.IsPrerelease ? ReleaseChannel.Beta : ReleaseChannel.Stable,
            };
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

    private static (Version, string) ParseVersionFromTag(string tag)
    {
        var tokens = tag.Split('.');
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

        return (version, buildNumber);
    }
}
