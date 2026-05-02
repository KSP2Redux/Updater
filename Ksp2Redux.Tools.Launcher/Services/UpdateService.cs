using System;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
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

    private static bool _isSingleFile;

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

    public UpdateService(ILauncherConfigService launcherConfigService, IFileSystem fileSystem, IEnvironmentProvider environmentProvider, IAssemblyService assemblyService, IMessageBoxService messageBoxService)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            new ProductHeaderValue("Ksp2Redux.Tools.Launcher", assemblyService.GetName().Version?.ToString() ?? "0.0.0")));
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        _assemblyService = assemblyService;
        _messageBoxService = messageBoxService;
        _launcherConfigService = launcherConfigService;
        _version = assemblyService.GetVersion();
        var uri = new Uri(launcherConfigService.Config.LauncherRepo.TrimEnd('/'));
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _owner = parts[0];
        _repo = parts[1];
#pragma warning disable IL3000
        _isSingleFile = string.IsNullOrEmpty(_assemblyService.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
    }

    /// <summary>
    /// Checks for an update from the provided GitHub repository, and downloads and installs it later
    /// </summary>
    /// <returns>A boolean whether to allow updating redux versions after this</returns>
    public async Task<bool> CheckAndPerformUpdateAsync()
    {
        Console.WriteLine("Checking for updates.");
        var releases = await _http.GetFromJsonAsync<GitHubRelease[]>(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases") ?? [];

        var latestRelease = releases.Where(r => r.TagName.StartsWith("updater-v") && !r.Prerelease)
            .Select(r =>
            {
                var versionPart = r.TagName.Replace("updater-v", "");
                return Version.TryParse(versionPart, out var v) ? new { Release = r, Version = v } : null;
            })
            .Where(v => v != null)
            .OrderByDescending(v => v!.Version)
            .FirstOrDefault();

        if (latestRelease != null && latestRelease.Version > _version)
        {
            Console.WriteLine($"Update found, v{latestRelease.Version}");

            if (!_isSingleFile)
            {
                Console.WriteLine("Running in non-single-file version somehow, will not perform update");
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

            if (asset != null)
            {
                const string sha256Prefix = "sha256:";
                if (asset.Digest == null || !asset.Digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"No sha256 digest reported by GitHub for {asset.Name}, refusing to update.");
                    await ShowUpdateFailedAsync();
                    return false;
                }

                var expectedHash = asset.Digest[sha256Prefix.Length..].Trim().ToLowerInvariant();

                DownloadingChanged?.Invoke(true);
                var restartTriggered = false;
                try
                {
                    var bytes = await _http.GetByteArrayAsync(asset.BrowserDownloadUrl);
                    var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Checksum mismatch for {asset.Name}: expected {expectedHash}, got {actualHash}");
                        await ShowUpdateFailedAsync();
                        return false;
                    }

                    var updateDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "update");
                    _fileSystem.Directory.CreateDirectory(updateDir);
                    foreach (var stale in _fileSystem.Directory.EnumerateFiles(updateDir))
                    {
                        try { _fileSystem.File.Delete(stale); } catch { }
                    }

                    var updatePath = _fileSystem.Path.Combine(updateDir, asset.Name);
                    await _fileSystem.File.WriteAllBytesAsync(updatePath, bytes);

                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        await Process.Start("chmod", $"+x \"{updatePath}\"").WaitForExitAsync();
                    }

                    TriggerRestart(updatePath);
                    restartTriggered = true;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Update download/install failed: {e}");
                    await ShowUpdateFailedAsync();
                    return false;
                }
                finally
                {
                    if (!restartTriggered) DownloadingChanged?.Invoke(false);
                }
            }
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
            Console.WriteLine($"Failed to open releases page: {e}");
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