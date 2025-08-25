using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ksp2Redux.Tools.Launcher.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Settings;

public partial class SettingsTabViewModel(LauncherConfig config) : ViewModelBase
{
    public string DisplayedInstallPath => config.Ksp2InstallPath;
    public ReleaseChannel ReleaseChannel
    {
        get => config.ReleaseChannel;
        set
        {
            config.ReleaseChannel = value;
            config.Save();
        }
    }

    readonly LauncherConfig config = config;

    private const string STEAM_INSTALL_DIR = "C:/Program Files (x86)/Steam/steamapps/common/Kerbal Space Program 2/KSP2_x64.exe";

    private static readonly FilePickerFileType Ksp2Exe = new("KSP2 installation")
    {
        Patterns = ["KSP2_x64.exe"],
    };

    public async Task SelectGameInstallDirectory()
    {

        var chosenPath = await DoOpenFilePickerAsync();
        if (chosenPath is not null)
        {
            config.Ksp2InstallPath = chosenPath.Path.LocalPath;
            config.Save();
            // TODO: trigger update patch status
        }
        //return config.Ksp2InstallPath;
    }

    public async Task<IStorageFile?> DoOpenFilePickerAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");

        IStorageFolder? startFolder = null;
        // default to previously select install path if it exists.
        if (!string.IsNullOrWhiteSpace(config.Ksp2InstallPath) && Path.Exists(config.Ksp2InstallPath))
        {
            startFolder = await provider.TryGetFolderFromPathAsync(config.Ksp2InstallPath);
        }

        // fallback on steam default path.
        startFolder ??= await provider.TryGetFolderFromPathAsync(STEAM_INSTALL_DIR);
        // fallback on something reasonable.
        startFolder ??= await provider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop);

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = "Open KSP2_x64.exe",
            AllowMultiple = false,
            FileTypeFilter = [Ksp2Exe],
            SuggestedStartLocation = startFolder,
        });

        return files?.Count >= 1 ? files[0] : null;
    }
}