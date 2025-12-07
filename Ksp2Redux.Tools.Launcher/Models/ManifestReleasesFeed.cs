using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Models;

public class ManifestReleasesFeed
{
    private readonly string BaseFilePath;
    private readonly string downloadStorageDir;
    private readonly string githubRelativeRepoUri;
    private readonly HttpClient apiClient;

    private Manifest? manifest;

    public ReleaseChannel CurrentChannel { get; private set; }

    public ManifestReleasesFeed(
        string BaseFilePath, string githubRelativeRepoUri, string personalAccessToken, string downloadStorageDir)
    {
        this.BaseFilePath = BaseFilePath;
        this.downloadStorageDir = downloadStorageDir;
        this.githubRelativeRepoUri = githubRelativeRepoUri;
        apiClient = new()
        {
            BaseAddress = new Uri("https://api.github.com/repos/" + githubRelativeRepoUri + "/"),
        };
        ProductHeaderValue header = new("Ksp2ReduxLauncher",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString());
        ProductInfoHeaderValue userAgent = new(header);
        apiClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
        apiClient.DefaultRequestHeaders.Accept.Add(new("application/vnd.github.v3.raw"));
        if (!string.IsNullOrWhiteSpace(personalAccessToken))
        {
            apiClient.DefaultRequestHeaders.Authorization = new("Bearer", personalAccessToken);
        }
    }

    public class Patch
    {
        public string version { get; set; }
        public string type { get; set; }
        public Requires requires { get; set; }
        public string url { get; set; }
        public string checksum_sha256 { get; set; }
        public int size { get; set; }
        public DateTime releasedAt { get; set; }

        public GameVersion ParseVersion()
        {
            var tokens = version.Split(new[] { '.', '-' });
            // remove optional leading 'v' from version
            if (tokens[0][0] == 'v')
            {
                tokens[0] = tokens[0][1..];
            }

            Version versionNumber;
            string buildNumber;
            if (tokens.Length > 4)
            {
                versionNumber = new Version(string.Join('.', tokens[0..4]));
                buildNumber = tokens[4];
            }
            else
            {
                versionNumber = new Version(string.Join('.', tokens));
                buildNumber = "0";
            }

            return new GameVersion()
            {
                VersionNumber = versionNumber,
                BuildNumber = buildNumber
            };
        }
    }

    public class Requires
    {
        public string? distribution { get; set; }
        public string? version { get; set; }
    }

    public class Manifest
    {
        public int schemaVersion { get; set; }
        public string channel { get; set; }
        public DateTime generatedAt { get; set; }
        public List<Patch> patches { get; set; }
    }


    public async Task UpdateManifest(ReleaseChannel channel)
    {
        var response = await apiClient.GetAsync(
            $"contents/Manifest.{channel}.json?ref=main");
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        response.EnsureSuccessStatusCode();
        manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(await response.Content.ReadAsStringAsync());
        CurrentChannel = channel;
    }

    public IEnumerable<GameVersion> GetAllVersions()
    {
        if (manifest?.patches != null)
            foreach (var release in manifest.patches)
            {
                var pversion = release.ParseVersion();
                pversion.Channel = CurrentChannel;
                yield return pversion;
            }
    }

    //if user selects the initial version as to install then skip later patch checking as only 1 patch needed
    public List<Patch> GetPatchListToVersion(string? distribution, GameVersion toGameVersion)
    {
        if (manifest?.patches is null)
            return new List<Patch>();

        Patch? matchingPrepatch = null;
        foreach (var release in manifest.patches)
        {
            if (!string.IsNullOrWhiteSpace(release.requires?.distribution) &&
                string.Equals(release.requires.distribution, distribution, StringComparison.OrdinalIgnoreCase))
            {
                matchingPrepatch = release;
                break;
            }
        }

        if (matchingPrepatch is null)
            return new List<Patch>();

        var prepatchVersion = matchingPrepatch.ParseVersion();

        bool numericBuild = !string.IsNullOrEmpty(toGameVersion.BuildNumber) &&
                            toGameVersion.BuildNumber.All(char.IsDigit);
        string sep = numericBuild ? "." : "-";
        string targetVersion = $"{toGameVersion.VersionNumber}{sep}{toGameVersion.BuildNumber}";
        if (string.Equals(matchingPrepatch.version, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new List<Patch> { matchingPrepatch };
        }

        return GetPatchListToVersion(prepatchVersion, toGameVersion);
    }
    //find the best path to use to get user to the correct game version from their game version
    public List<Patch> GetPatchListToVersion(GameVersion fromGameVersion, GameVersion toGameVersion)
    {
        if (manifest?.patches is null) return new List<Patch>();

        static string ToVersionString(GameVersion gv)
        {
            bool numericBuild = !string.IsNullOrEmpty(gv.BuildNumber) && gv.BuildNumber.All(char.IsDigit);
            string sep = numericBuild ? "." : "-";
            return $"{gv.VersionNumber}{sep}{gv.BuildNumber}";
        }

        string startVersion = ToVersionString(fromGameVersion);
        string targetVersion = ToVersionString(toGameVersion);

        var patchesByOutput = manifest.patches
            .Where(p => !string.IsNullOrWhiteSpace(p.version))
            .GroupBy(p => p.version, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (!patchesByOutput.ContainsKey(targetVersion))
            return new List<Patch>();

        var queue = new Queue<(string version, List<Patch> path)>();

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        queue.Enqueue((targetVersion, new List<Patch>()));
        visited.Add(targetVersion);

        while (queue.Count > 0)
        {
            var (currentVer, currentPath) = queue.Dequeue();

            if (string.Equals(currentVer, startVersion, StringComparison.OrdinalIgnoreCase))
            {
                currentPath.Reverse();
                return currentPath;
            }

            if (patchesByOutput.TryGetValue(currentVer, out var producers))
            {
                foreach (var patch in producers.OrderByDescending(p =>
                             string.Equals(p.type, "delta", StringComparison.OrdinalIgnoreCase)))
                {
                    var requiredVer = patch.requires?.version;

                    if (string.IsNullOrWhiteSpace(requiredVer) || visited.Contains(requiredVer))
                        continue;

                    visited.Add(requiredVer);

                    var newPath = new List<Patch>(currentPath) { patch };
                    queue.Enqueue((requiredVer, newPath));
                }
            }
        }

        return new List<Patch>();
    }

    public async Task<string> DownloadPatch(Patch patch, Action<string> log, Action<long, long> reportDownloadProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string FileName = patch.url.Split("/").Last();
        string patchDownloadTo = Path.Combine(downloadStorageDir, FileName);

        log($"Downloading {FileName}");
        reportDownloadProgress(0, patch.size);

        if (!File.Exists(patchDownloadTo) || new FileInfo(patchDownloadTo).Length != patch.size)
        {
            string assetApiUrl = await GetAssetApiUrl(patch.url, ct);

            using var apiRequest = new HttpRequestMessage(HttpMethod.Get, assetApiUrl);
            apiRequest.Headers.Accept.Add(new("application/octet-stream"));

            using var apiResponse = await apiClient.SendAsync(apiRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            Uri downloadUri;

            if (apiResponse.StatusCode == System.Net.HttpStatusCode.Redirect ||
                apiResponse.StatusCode == System.Net.HttpStatusCode.Found ||
                apiResponse.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
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

            using var downloadResponse =
                await cleanClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
            downloadResponse.EnsureSuccessStatusCode();

            long contentLength = downloadResponse.Content.Headers.ContentLength ?? patch.size;

            using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(patchDownloadTo, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long totalBytesRead = 0;
            int bytesRead;
            var updateTimer = Stopwatch.StartNew();

            while ((bytesRead = await downloadStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalBytesRead += bytesRead;

                if (updateTimer.ElapsedMilliseconds > 100)
                {
                    reportDownloadProgress(totalBytesRead, contentLength);
                    updateTimer.Restart();
                }
            }
        }
        else
        {
            reportDownloadProgress(patch.size, patch.size);
        }

        log("Download complete.");
        return patchDownloadTo;
    }

    private async Task<string> GetAssetApiUrl(string browserUrl, CancellationToken ct)
    {
        var uri = new Uri(browserUrl);
        var segments = uri.Segments;

        string tag = Uri.UnescapeDataString(segments[^2].Trim('/'));
        string fileName = Uri.UnescapeDataString(segments[^1]);

        using var response = await apiClient.GetAsync($"releases/tags/{tag}", ct);
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