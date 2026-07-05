using System;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Services;


public interface IUpdateService
{

    public Task<bool> CheckAndPerformUpdateAsync();

    event Action<bool>? DownloadingChanged;
}

/// <summary>
/// Service for checking updates to the launcher specifically, will use the AssemblyVersion property
/// </summary>
public class UpdateService : IUpdateService
{
    public event Action<bool>? DownloadingChanged;

    private HttpClient _http;
    private string _owner;
    private string _repo;
    private Version? _version;
    private IFileSystem _fileSystem;
    private IEnvironmentProvider _environmentProvider;
    private IAssemblyService _assemblyService;
    private ILauncherConfigService _launcherConfigService;
    private IMessageBoxService _messageBoxService;
    private ILogService _log;

    private static bool _isSingleFile;
    private bool _checkInProgress;

    private class GitHubReleaseAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public GitHubReleaseAsset[] Assets { get; set; } = [];
    }

    public UpdateService(ILauncherConfigService launcherConfigService, IFileSystem fileSystem, IEnvironmentProvider environmentProvider, IAssemblyService assemblyService, IMessageBoxService messageBoxService, ILogService log)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            new ProductHeaderValue("Ksp2Redux.Tools.Launcher", assemblyService.GetName().Version?.ToString() ?? "0.0.0")));
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        _assemblyService = assemblyService;
        _messageBoxService = messageBoxService;
        _launcherConfigService = launcherConfigService;
        _log = log;
        _version = assemblyService.GetVersion();
        var uri = new Uri(launcherConfigService.Config.LauncherRepo.TrimEnd('/'));
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _owner = parts[0];
        _repo = parts[1];
#pragma warning disable IL3000
        _isSingleFile = string.IsNullOrEmpty(_assemblyService.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
        _log.Info($"UpdateService initialized. Repo={_owner}/{_repo}, Version={_version}, SingleFile={_isSingleFile}");
    }

    /// <summary>
    /// Checks for an update from the provided GitHub repository, and downloads and installs it later
    /// </summary>
    /// <returns>A boolean whether to allow updating redux versions after this</returns>
    public async Task<bool> CheckAndPerformUpdateAsync()
    {
        // Called on a 10-minute timer as well as at startup and from a manual button. If a check
        // (and its "Update Found" dialog) is still awaiting a response, skip instead of stacking
        // another dialog on top - left running overnight this used to pile up dozens of them.
        if (_checkInProgress)
        {
            _log.Info("An update check is already in progress, skipping this one.");
            return true;
        }
        _checkInProgress = true;
        try
        {
            return await CheckAndPerformUpdateCoreAsync();
        }
        finally
        {
            _checkInProgress = false;
        }
    }

    private async Task<bool> CheckAndPerformUpdateCoreAsync()
    {
        var releasesUrl = $"https://api.github.com/repos/{_owner}/{_repo}/releases";
        _log.Info($"Checking for launcher updates from {releasesUrl} (current version {_version}).");

        GitHubRelease[] releases;
        try
        {
            releases = await _http.GetFromJsonAsync<GitHubRelease[]>(releasesUrl) ?? [];
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to fetch releases from {releasesUrl}. Skipping update check.", ex);
            return true;
        }
        _log.Info($"GitHub returned {releases.Length} release(s).");

        var matchingReleases = releases.Where(r => r.TagName.StartsWith("updater-v") && !r.Prerelease)
            .Select(r =>
            {
                var versionPart = r.TagName.Replace("updater-v", "");
                return Version.TryParse(versionPart, out var v) ? new { Release = r, Version = v } : null;
            })
            .Where(v => v != null)
            .OrderByDescending(v => v!.Version)
            .ToList();

        if (matchingReleases.Count == 0)
        {
            _log.Warn("No releases matching the 'updater-v' tag prefix were found. Nothing to update to.");
            return true;
        }

        var latestRelease = matchingReleases.FirstOrDefault();
        _log.Info($"Latest available updater release: v{latestRelease!.Version} (current: {_version}).");

        if (latestRelease.Version > _version)
        {
            _log.Info($"Update available: v{latestRelease.Version}.");

            if (!_isSingleFile)
            {
                _log.Warn("Running in non-single-file build, refusing to self-update.");
                await _messageBoxService.ShowMessageBoxAsOwnedAsync("Update Found",
                    "You are not running in a single file build, rebuild from the latest main to be able to install Redux.", ButtonEnum.Ok,
                    windowStartupLocation: WindowStartupLocation.CenterOwner);
                return false;
            }

            var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync("Update Found",
                "The launcher will download and update, it may restart a few times during this.\nWithout updating you cannot install new Redux versions.", ButtonEnum.OkCancel,
                windowStartupLocation: WindowStartupLocation.CenterOwner);

            if (result != ButtonResult.Ok) return false;

            var platformKeyword = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "linux";
            var asset = latestRelease.Release.Assets
                .FirstOrDefault(a => a.Name.Contains(platformKeyword, StringComparison.OrdinalIgnoreCase));

            if (asset == null)
            {
                _log.Warn($"No asset matched platform keyword '{platformKeyword}' in release v{latestRelease.Version}. Available assets: {string.Join(", ", latestRelease.Release.Assets.Select(a => a.Name))}");
            }
            else
            {
                _log.Info($"Selected update asset: {asset.Name}.");
                const string sha256Prefix = "sha256:";
                if (asset.Digest == null || !asset.Digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Error($"No sha256 digest reported by GitHub for {asset.Name}, refusing to update.");
                    await ShowUpdateFailedAsync();
                    return false;
                }

                var expectedHash = asset.Digest[sha256Prefix.Length..].Trim().ToLowerInvariant();

                DownloadingChanged?.Invoke(true);
                var restartTriggered = false;
                try
                {
                    _log.Info($"Downloading {asset.Name} from {asset.BrowserDownloadUrl}.");
                    var bytes = await _http.GetByteArrayAsync(asset.BrowserDownloadUrl);
                    var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Error($"Checksum mismatch for {asset.Name}: expected {expectedHash}, got {actualHash}");
                        await ShowUpdateFailedAsync();
                        return false;
                    }
                    _log.Info($"Downloaded {bytes.Length} bytes for {asset.Name}, checksum verified.");

                    var updateDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "update");
                    _fileSystem.Directory.CreateDirectory(updateDir);
                    foreach (var stale in _fileSystem.Directory.EnumerateFiles(updateDir))
                    {
                        try { _fileSystem.File.Delete(stale); } catch { }
                    }

                    var updatePath = _fileSystem.Path.Combine(updateDir, asset.Name);
                    await _fileSystem.File.WriteAllBytesAsync(updatePath, bytes);
                    _log.Info($"Wrote update binary to {updatePath}. Triggering restart.");

                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        await Process.Start("chmod", $"+x \"{updatePath}\"").WaitForExitAsync();
                    }

                    TriggerRestart(updatePath);
                    restartTriggered = true;
                }
                catch (Exception e)
                {
                    _log.Error($"Update download/install failed for {asset.Name}.", e);
                    await ShowUpdateFailedAsync();
                    return false;
                }
                finally
                {
                    if (!restartTriggered) DownloadingChanged?.Invoke(false);
                }
            }
        }
        else
        {
            _log.Info("Launcher is already up to date.");
        }

        return true;
    }
    
    private async Task ShowUpdateFailedAsync()
    {
        var repo = _launcherConfigService.Config.LauncherRepo.TrimEnd('/');
        var releasesUrl = $"{repo}/releases";
        await _messageBoxService.ShowMessageBoxAsOwnedAsync("Update Failed!",
            $"Please download the latest version of the launcher from\n{releasesUrl}", ButtonEnum.Ok,
            windowStartupLocation: WindowStartupLocation.CenterOwner);
        try
        {
            Process.Start(new ProcessStartInfo(releasesUrl) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log.Error($"Failed to open releases page {releasesUrl}.", e);
        }
    }

    private void TriggerRestart(string newFilesPath)
    {
        var whereAmI = _environmentProvider.ProcessPath!;
        var args = $"--pid {_environmentProvider.ProcessId} --exe \"{_fileSystem.Path.GetFullPath(whereAmI)}\"";
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = _fileSystem.Path.GetDirectoryName(newFilesPath)
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = newFilesPath;
            startInfo.Arguments = args;
        }
        else
        {
            startInfo.FileName = "setsid";
            startInfo.Arguments = $"-f \"{newFilesPath}\" {args}";
        }

        Process.Start(startInfo);
        _environmentProvider.Exit(0);
    }
}