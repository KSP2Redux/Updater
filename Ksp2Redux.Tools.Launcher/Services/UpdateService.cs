using System;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Octokit;

namespace Ksp2Redux.Tools.Launcher.Services;


public interface IUpdateService
{
    public Task CheckAndPerformUpdateAsync(bool autoRestart);
}

/// <summary>
/// Service for checking updates to the launcher specifically, will use the AssemblyVersion property
/// </summary>
public class UpdateService : IUpdateService
{
    private GitHubClient _client;
    private string _owner;
    private string _repo;
    private Version? _version;
    private IFileSystem _fileSystem;
    private IEnvironmentProvider _environmentProvider;

    public UpdateService(ILauncherConfigService launcherConfigService, IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    {
        _client = new GitHubClient(new ProductHeaderValue("Ksp2Redux.Tools.Launcher"));
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        var uri = new Uri(launcherConfigService.Config.LauncherRepo.TrimEnd('/'));
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _owner = parts[0];
        _repo = parts[1];
        _version = new Version(Constants.Version);
    }

    /// <summary>
    /// Checks for an update from the provided GitHub repository, and downloads and installs it later
    /// </summary>
    /// <param name="autoRestart">true only when this is dispatched on the launch of the program, not the every 10 minute check</param>
    public async Task CheckAndPerformUpdateAsync(bool autoRestart)
    {
        var releases = await _client.Repository.Release.GetAll(_owner, _repo);
        
        var latestRelease = releases.Where(r => r.TagName.StartsWith("launcher-v") && !r.Prerelease)
            .Select(r =>
            {
                var versionPart = r.TagName.Replace("launcher-v", "");
                return Version.TryParse(versionPart, out var v) ? new { Release = r, Version = v } : null;
            })
            .Where(v => v != null)
            .OrderByDescending(v => v!.Version)
            .FirstOrDefault();

        if (latestRelease != null && latestRelease.Version > _version)
        {
            if (!autoRestart)
            {
                await MessageBoxManager.GetMessageBoxStandard("Update Found",
                    "The launcher will download and update, it may restart a few times during this.", ButtonEnum.Ok,
                    windowStartupLocation: WindowStartupLocation.CenterOwner).ShowAsync();
            }
            
            var platformKeyword = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "linux";
            var asset = latestRelease.Release.Assets
                .FirstOrDefault(a => a.Name.Contains(platformKeyword, StringComparison.OrdinalIgnoreCase));

            if (asset != null)
            {
                var tempPath = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), asset.Name);
                using (var httpClient = new HttpClient())
                {
                   
                   var data = await httpClient.GetByteArrayAsync(asset.BrowserDownloadUrl);
                   await _fileSystem.File.WriteAllBytesAsync(tempPath, data);
                }
                
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await Process.Start("chmod", $"+x \"{tempPath}\"").WaitForExitAsync();
                }

                TriggerRestart(tempPath);
            }
        }
    }
    
    private void TriggerRestart(string newFilesPath)
    {
        var whereAmI = _environmentProvider.ProcessPath!;
        var startInfo = new ProcessStartInfo(newFilesPath)
        {
            Arguments = $"--pid {_environmentProvider.ProcessId} --exe \"{_fileSystem.Path.GetFullPath(whereAmI)}\"",
            UseShellExecute = true,
            WorkingDirectory = _fileSystem.Path.GetDirectoryName(newFilesPath),
            WindowStyle = ProcessWindowStyle.Normal
        };
        
        Process.Start(startInfo);
        _environmentProvider.Exit(0);
    }
}