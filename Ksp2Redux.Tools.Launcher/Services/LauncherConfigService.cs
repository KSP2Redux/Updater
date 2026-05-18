using System;
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
    private readonly ILogService _log;

    public LauncherConfig Config { get; set; }
    private readonly JsonSerializerOptions StorageOptions = new() { WriteIndented = true };

    private const string LauncherConfigJson = "redux-launcher-config.json";

    public LauncherConfigService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider, ILogService log)
    {
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        _log = log;
        GetOrCreateCurrentConfig(_fileSystem);
    }

    private void GetOrCreateCurrentConfig(IFileSystem fileSystem)
    {
        var storageDir = GetLocalStorageDirectory();
        _fileSystem.Directory.CreateDirectory(storageDir);
        var configFilePath = GetConfigFilePath();
        _log.Info($"Loading launcher config from {configFilePath}");

        LauncherConfig? config = null;

        try
        {
            config = JsonSerializer.Deserialize<LauncherConfig>(fileSystem.File.ReadAllText(configFilePath));
            if (config is null)
            {
                _log.Warn($"Config file deserialized to null at {configFilePath}. A fresh config will be created.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read or deserialize launcher config at {configFilePath} ({ex.GetType().Name}: {ex.Message}). A fresh config will be created.");
        }

        if (config is null)
        {
            Config = new(configFilePath);
            Save();
            _log.Info("Fresh launcher config written to disk.");
        }
        else
        {
            Config = config;
            Config.StoragePath = configFilePath;
            _log.Info($"Launcher config loaded. Installs={Config.Ksp2Installs.Count}, Feeds={Config.Feeds.Count}, ActiveInstall={Config.ActiveKsp2InstallId}");
            if (MigrateLegacySingleInstall())
            {
                _log.Info("Launcher config migrated from legacy single-install schema.");
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
        try
        {
            _fileSystem.File.WriteAllText(Config.StoragePath, JsonSerializer.Serialize(Config, StorageOptions));
            _log.Info($"Launcher config saved to {Config.StoragePath}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save launcher config to {Config.StoragePath}", ex);
            throw;
        }
    }

    private string GetConfigFilePath()
    {
        return _fileSystem.Path.Combine(GetLocalStorageDirectory(), LauncherConfigJson);
    }

    public string GetLocalStorageDirectory()
    {
        return LocalStoragePaths.GetLocalStorageDirectory(_fileSystem, _environmentProvider);
    }
}