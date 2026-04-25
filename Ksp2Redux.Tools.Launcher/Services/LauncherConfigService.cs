using System;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface ILauncherConfigService
{
    LauncherConfig Config { get; }
    void Save();
    string GetLocalStorageDirectory();
}

public class LauncherConfigService : ILauncherConfigService
{
    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentProvider _environmentProvider;
    
    public LauncherConfig Config { get; set; }
    private readonly JsonSerializerOptions StorageOptions = new() { WriteIndented = true };
    
    private const string ReduxLauncherConfigFolder = "Ksp2Redux";
    private const string LauncherConfigJson = "redux-launcher-config.json";

    public LauncherConfigService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    {
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        GetOrCreateCurrentConfig(_fileSystem);
    }

    private void GetOrCreateCurrentConfig(IFileSystem fileSystem)
    {
        _fileSystem.Directory.CreateDirectory(GetLocalStorageDirectory());
        var configFilePath = GetConfigFilePath();

        LauncherConfig? config = null;

        try
        {
            config = JsonSerializer.Deserialize<LauncherConfig>(fileSystem.File.ReadAllText(configFilePath));
        }
        catch
        {
            Debug.Write("Can't load configuration.  Creating a new one.");
        }

        if (config is null)
        {
            Config = new(configFilePath);
            Save();
        }
        else
        {
            Config = config;
            Config.StoragePath = configFilePath;
            if (MigrateLegacySingleInstall())
            {
                Save();
            }
        }
    }

    private bool MigrateLegacySingleInstall()
    {
        var didMigrate = false;
        if (Config.Ksp2Installs.Count == 0 && !string.IsNullOrWhiteSpace(Config.Ksp2InstallPath))
        {
            var entry = new Ksp2InstallEntry
            {
                ExePath = Config.Ksp2InstallPath,
                ReleaseChannel = string.IsNullOrEmpty(Config.ReleaseChannel) ? "beta" : Config.ReleaseChannel,
                LastInstalledVersion = Config.LastInstalledVersion,
                Name = DeriveDefaultInstallName(Config.Ksp2InstallPath),
                LaunchThroughSteam = Config.LaunchThroughSteam,
                SteamAppId = string.IsNullOrEmpty(Config.SteamAppId) ? "954850" : Config.SteamAppId,
                LaunchArguments = Config.LaunchArguments,
            };
            Config.Ksp2Installs.Add(entry);
            Config.ActiveKsp2InstallId = entry.Id;
            Config.Ksp2InstallPath = "";
            Config.LastInstalledVersion = null;
            didMigrate = true;
        }

        if (Config.Ksp2Installs.Count > 0 &&
            (Config.ActiveKsp2InstallId is null ||
             Config.Ksp2Installs.TrueForAll(e => e.Id != Config.ActiveKsp2InstallId)))
        {
            Config.ActiveKsp2InstallId = Config.Ksp2Installs[0].Id;
            didMigrate = true;
        }

        return didMigrate;
    }

    public static string DeriveDefaultInstallName(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "KSP2";
        var trimmed = exePath.TrimEnd('/', '\\');
        var lastSep = trimmed.LastIndexOfAny(['/', '\\']);
        if (lastSep <= 0) return "KSP2";
        var dir = trimmed[..lastSep];
        var prevSep = dir.LastIndexOfAny(['/', '\\']);
        var leaf = prevSep < 0 ? dir : dir[(prevSep + 1)..];
        return string.IsNullOrEmpty(leaf) ? "KSP2" : leaf;
    }
    
    public void Save()
    {
        var directory = _fileSystem.Path.GetDirectoryName(Config.StoragePath);
        _fileSystem.Directory.CreateDirectory(directory!);
        _fileSystem.File.WriteAllText(Config.StoragePath, JsonSerializer.Serialize(Config, StorageOptions));
    }

    private string GetConfigFilePath()
    {
        return _fileSystem.Path.Combine(GetLocalStorageDirectory(), LauncherConfigJson);
    }

    public string GetLocalStorageDirectory()
    {
        var appdataPath = _environmentProvider.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return _fileSystem.Path.Combine(appdataPath, ReduxLauncherConfigFolder);
    }
}