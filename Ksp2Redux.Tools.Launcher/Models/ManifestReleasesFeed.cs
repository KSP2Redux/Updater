using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Models;

public class ManifestReleasesFeed
{
    private readonly IFileSystem _fileSystem;
    private readonly string BaseFilePath;
    private readonly string downloadStorageDir;
    private readonly string githubRelativeRepoUri;
    private readonly HttpClient apiClient;
    private readonly string manifestPath;

    private Manifest? manifest;

    public String CurrentChannel { get; private set; }

    public ManifestReleasesFeed(
        IFileSystem fileSystem, string BaseFilePath, string githubRelativeRepoUri, string downloadStorageDir,
        string manifestPath, string? token = null)
    {
        _fileSystem = fileSystem;
        this.BaseFilePath = BaseFilePath;
        this.downloadStorageDir = downloadStorageDir;
        this.githubRelativeRepoUri = githubRelativeRepoUri;
        this.manifestPath = manifestPath;
        apiClient = new()
        {
            BaseAddress = new Uri("https://api.github.com/repos/" + githubRelativeRepoUri + "/"),
        };
        ProductHeaderValue header = new("Ksp2ReduxLauncher",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString());
        ProductInfoHeaderValue userAgent = new(header);
        apiClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
        apiClient.DefaultRequestHeaders.Accept.Add(new("application/vnd.github.v3.raw"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            apiClient.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
    }

    public class Patch
    {
        public string version { get; set; }
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
            string CommitHash;
            if (tokens.Length > 4)
            {
                versionNumber = new Version(string.Join('.', tokens[0..4]));
                CommitHash = tokens[4];
            }
            else
            {
                versionNumber = new Version(string.Join('.', tokens));
                CommitHash = "0";
            }

            return new GameVersion()
            {
                VersionNumber = versionNumber,
                BuildNumber = CommitHash,
                CommitHash = CommitHash
            };
        }
    }

    public class Requires
    {
        public string? version { get; set; }

        [JsonIgnore] public bool IsBasePatch => string.IsNullOrEmpty(version);
    }

    public class Manifest
    {
        public int schemaVersion { get; set; }
        public string channel { get; set; }
        public DateTime generatedAt { get; set; }
        public List<Patch> patches { get; set; }
    }


    public async Task UpdateManifest()
    {
        var response = await apiClient.GetAsync(
            $"contents/{manifestPath}?ref=main");
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        // Console.WriteLine($"Final url: {finalUrl}");
        response.EnsureSuccessStatusCode();
        manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(await response.Content.ReadAsStringAsync());
        CurrentChannel = manifest.channel;
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

    // private InstallPlan GetBestFromPrepatchToVersion(GameVersion toGameVersion)
    // {
    //     var lowestCostSoFar = int.MaxValue;
    //     InstallPlan bestPlan = new InstallPlan();
    //     foreach (var release in manifest.patches)
    //     {
    //         if (string.Equals())
    //     }
    // }
    
    //if user selects the initial version as to install then skip later patch checking as only 1 patch needed
    // public InstallPlan GetPatchListToVersion(GameVersion toGameVersion)
    // {
    //     
    //     var plan = new InstallPlan();
    //     if (manifest?.patches is null)
    //         return plan;
    //
    //     
    //     // Patch? matchingPrepatch = null;
    //     // foreach (var release in manifest.patches)
    //     // {
    //     //     if (!string.IsNullOrWhiteSpace(release.requires?.distribution) &&
    //     //         string.Equals(release.requires.distribution, distribution, StringComparison.OrdinalIgnoreCase))
    //     //     {
    //     //         matchingPrepatch = release;
    //     //         break;
    //     //     }
    //     // }
    //
    //     var prepatchVersion = matchingPrepatch.ParseVersion();
    //
    //     bool numericBuild = !string.IsNullOrEmpty(toGameVersion.CommitHash) &&
    //                         toGameVersion.CommitHash.All(char.IsDigit);
    //     string sep = numericBuild ? "." : "-";
    //     string targetVersion = $"{toGameVersion.VersionNumber}{sep}{toGameVersion.CommitHash}";
    //     if (string.Equals(matchingPrepatch.version, targetVersion, StringComparison.OrdinalIgnoreCase))
    //     {
    //         return new List<Patch> { matchingPrepatch };
    //     }
    //
    //     var patchList = GetPatchListToVersion(prepatchVersion, toGameVersion);
    //     patchList.Reverse();
    //     patchList.Add(matchingPrepatch);
    //     patchList.Reverse();
    //     return patchList;
    // }
    //find the best path to use to get user to the correct game version from their game version
    
    
    public InstallPlan GetPatchListToVersion(GameVersion fromGameVersion, GameVersion toGameVersion)
    {
        if (manifest?.patches is null) return new InstallPlan(_fileSystem);

        static string ToVersionString(GameVersion gv)
        {
            bool numericBuild = !string.IsNullOrEmpty(gv.CommitHash) && gv.CommitHash.All(char.IsDigit);
            string sep = numericBuild ? "." : "-";
            return $"{gv.VersionNumber}{sep}{gv.CommitHash}";
        }

        string startVersion = ToVersionString(fromGameVersion);
        string targetVersion = ToVersionString(toGameVersion);

        var patchesByOutput = manifest.patches
            .Where(p => !string.IsNullOrWhiteSpace(p.version))
            .GroupBy(p => p.version, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (!patchesByOutput.ContainsKey(targetVersion))
            return new InstallPlan(_fileSystem);

        if (GetPlan(startVersion, targetVersion, new InstallPlan(_fileSystem)) is {} result)
        {
            return result;
        }
        
        return new InstallPlan(_fileSystem);

        InstallPlan? GetPlan(string from, string to, InstallPlan initialPlan)
        {
            InstallPlan? bestPlan = null;

            if (patchesByOutput.TryGetValue(to, out var patches))
            {
                foreach (var patch in patches)
                {
                    if (patch.requires.version == from)
                    {
                        bestPlan = new InstallPlan(_fileSystem);
                        bestPlan.ApplyPatchFile((log, progress,ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from version {from}");
                        break;
                    }
                    
                    if (patch.requires.IsBasePatch)
                    {
                        var testPlan = new InstallPlan(_fileSystem);
                        testPlan.ApplyPatchFile((log, progress, ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from prepatch");
                        testPlan.Prepatch();
                        testPlan.RevertToStock();
                        if (bestPlan == null || bestPlan.Cost > testPlan.Cost) bestPlan = testPlan;
                    }
                    else
                    {
                        var newInitialPlan = new InstallPlan(_fileSystem);
                        newInitialPlan.ApplyPatchFile((log, progress, ct) => DownloadPatch(patch, log, progress, ct), $"applying patch for version: {to} from version {patch.requires.version}");
                        var testPlan = GetPlan(from, patch.requires.version!, newInitialPlan);
                        if (testPlan != null && (bestPlan == null || bestPlan.Cost > testPlan.Cost)) bestPlan = testPlan;
                    }
                }
            }

            if (bestPlan == null) return null;
            return bestPlan + initialPlan;
        }
    }

    public async Task<string> DownloadPatch(Patch patch, Action<string> log, Action<long, long> reportDownloadProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string FileName = patch.url.Split("/").Last();
        string patchDownloadTo = _fileSystem.Path.Combine(downloadStorageDir, FileName);

        log($"Downloading {FileName}");
        reportDownloadProgress(0, patch.size);

        if (!_fileSystem.File.Exists(patchDownloadTo) || new FileInfo(patchDownloadTo).Length != patch.size)
        {
            string assetApiUrl = await GetAssetApiUrl(patch.url, ct);

            using var apiRequest = new HttpRequestMessage(HttpMethod.Get, assetApiUrl);
            apiRequest.Headers.Accept.Add(new("application/octet-stream"));

            using var apiResponse = await apiClient.SendAsync(apiRequest, HttpCompletionOption.ResponseHeadersRead, ct);

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