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
    public LauncherConfig Config { get; set; }
    private readonly JsonSerializerOptions StorageOptions = new() { WriteIndented = true };
    
    private const string ReduxLauncherConfigFolder = "Ksp2Redux";
    private const string LauncherConfigJson = "redux-launcher-config.json";

    public LauncherConfigService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        Config = GetOrCreateCurrentConfig(_fileSystem);
    }

    private LauncherConfig GetOrCreateCurrentConfig(IFileSystem fileSystem)
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
            config = new(configFilePath);
            Save();
        }
        else
        {
            config.StoragePath = configFilePath;
        }

        return config;
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
        var appdataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return _fileSystem.Path.Combine(appdataPath, ReduxLauncherConfigFolder);
    }
}