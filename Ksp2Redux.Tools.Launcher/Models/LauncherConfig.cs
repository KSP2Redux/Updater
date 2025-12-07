using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Ksp2Redux.Tools.Launcher.Models;

public class LauncherConfig
{
    public string Ksp2InstallPath { get; set; } = "";
    public string ReduxRepoUrl { get; set; } = "";
    public string Pat { get; set; } = "";
    public ReleaseChannel ReleaseChannel { get; set; }
    public GameVersion? LastInstalledVersion { get; set; }

    private string storagePath = string.Empty;

    private const string reduxLauncherConfigFolder = "Ksp2Redux";
    private const string launcherConfigJson = "redux-launcher-config.json";

    private static readonly JsonSerializerOptions storageOptions = new() { WriteIndented = true };

    public LauncherConfig() { }

    private LauncherConfig(string storagePath)
    {
        this.storagePath = storagePath;
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
            config.storagePath = configFilePath;
        }

        return config;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(storagePath);
        Directory.CreateDirectory(directory!);
        File.WriteAllText(storagePath, JsonSerializer.Serialize(this, storageOptions));
    }

    private static string GetConfigFilePath()
    {
        return Path.Combine(GetLocalStorageDirectory(), launcherConfigJson);
    }

    public static string GetLocalStorageDirectory()
    {
        var appdataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appdataPath, reduxLauncherConfigFolder);
    }
}
