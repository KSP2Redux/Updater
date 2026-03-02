using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Ksp2Redux.Tools.Launcher.Models;

public class LauncherConfig
{
    public string Ksp2InstallPath { get; set; } = "";
    public string ReleaseChannel { get; set; }
    public GameVersion? LastInstalledVersion { get; set; }
    public List<FeedInfo> Feeds { get; set; } = new();
    
    
    private string _storagePath = string.Empty;

    private const string ReduxLauncherConfigFolder = "Ksp2Redux";
    private const string LauncherConfigJson = "redux-launcher-config.json";

    private static readonly JsonSerializerOptions StorageOptions = new() { WriteIndented = true };

    public LauncherConfig() { }

    private LauncherConfig(string storagePath)
    {
        _storagePath = storagePath;
    }

    public static LauncherConfig GetOrCreateCurrentConfig()
    {
        Directory.CreateDirectory(GetLocalStorageDirectory());
        var configFilePath = GetConfigFilePath();

        LauncherConfig? config = null;

        try
        {
            config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configFilePath));
        }
        catch
        {
            Debug.Write("Can't load configuration.  Creating a new one.");
        }

        if (config is null)
        {
            config = new(configFilePath);
            config.Save();
        }
        else
        {
            config._storagePath = configFilePath;
        }

        return config;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_storagePath);
        Directory.CreateDirectory(directory!);
        File.WriteAllText(_storagePath, JsonSerializer.Serialize(this, StorageOptions));
    }

    private static string GetConfigFilePath()
    {
        return Path.Combine(GetLocalStorageDirectory(), LauncherConfigJson);
    }

    public static string GetLocalStorageDirectory()
    {
        var appdataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appdataPath, ReduxLauncherConfigFolder);
    }
}
