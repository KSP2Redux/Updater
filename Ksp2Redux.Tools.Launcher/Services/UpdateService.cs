using System;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Octokit;

namespace Ksp2Redux.Tools.Launcher.Services;


public interface IUpdateService
{
    
    public Task<bool> CheckAndPerformUpdateAsync();
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
    private IAssemblyService _assemblyService;
#pragma warning disable RS0030
#pragma warning disable IL3000
    private static bool _isSingleFile =  string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
#pragma warning restore IL3000
#pragma warning restore RS0030
    
    public UpdateService(ILauncherConfigService launcherConfigService, IFileSystem fileSystem, IEnvironmentProvider environmentProvider, IAssemblyService assemblyService)
    {
        _client = new GitHubClient(new ProductHeaderValue("Ksp2Redux.Tools.Launcher"));
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        _assemblyService = assemblyService;
        _version = assemblyService.GetVersion();
        var uri = new Uri(launcherConfigService.Config.LauncherRepo.TrimEnd('/'));
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _owner = parts[0];
        _repo = parts[1];
    }

    /// <summary>
    /// Checks for an update from the provided GitHub repository, and downloads and installs it later
    /// </summary>
    /// <returns>A boolean whether to allow updating redux versions after this</returns>
    public async Task<bool> CheckAndPerformUpdateAsync()
    {
        Console.WriteLine("Checking for updates.");
        var releases = await _client.Repository.Release.GetAll(_owner, _repo);
        
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
                return false;
            }
            
            var result = await MessageBoxManager.GetMessageBoxStandard("Update Found",
                "The launcher will download and update, it may restart a few times during this.\nWithout updating you cannot install new versions.", ButtonEnum.OkCancel,
                windowStartupLocation: WindowStartupLocation.CenterOwner).ShowAsync();

            if (result != ButtonResult.Ok) return false;
            
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

        return true;
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