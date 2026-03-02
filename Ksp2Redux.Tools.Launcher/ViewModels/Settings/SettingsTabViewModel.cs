using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ksp2Redux.Tools.Launcher.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Common;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Settings;

public partial class SettingsTabViewModel() : ViewModelBase
{
    private readonly MainWindowViewModel parentWindow;
    private readonly LauncherConfig config;
    public string DisplayedInstallPath => config.Ksp2InstallPath;
    public bool ChannelsLoaded = false;
    
    public string ReleaseChannel
    {
        get => ChannelsLoaded ? config.ReleaseChannel : "";
        set
        {
            if (!ChannelsLoaded) return;
            config.ReleaseChannel = value;
            config.Save();
            _ = parentWindow.HomeTab.UpdateVersionsList();
        }
    }

    public ObservableCollection<string> ValidChannels { get; } = [];

    public void SetLoaded()
    {
        ChannelsLoaded = true;
        ReleaseChannel = config.ReleaseChannel;
        OnPropertyChanged(nameof(ReleaseChannel));
    }
    
    public SettingsTabViewModel(LauncherConfig config,MainWindowViewModel parentWindow) : this()
    {
        this.parentWindow = parentWindow;
        this.config = config;
    }

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
        parentWindow.TryLoadKsp2Install();
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

    public async Task UninstallRedux()
    {
        parentWindow.TryLoadKsp2Install();
        if (parentWindow.Ksp2?.Distribution != Distribution.Redux)
        {
            await MessageBoxManager.GetMessageBoxStandard("Error!", "Redux is not installed...").ShowAsync();
            return;
        }
        
        var box = MessageBoxManager.GetMessageBoxStandard("Confirm", "Are you sure you want to uninstall Redux?",
            ButtonEnum.YesNo);
        
        
        var result = await box.ShowAsync();
        if (result != ButtonResult.Yes) return;
        
        Cache.RecursivelyRestoreCache(parentWindow.Ksp2.InstallDir);
        
        parentWindow.TryLoadKsp2Install();
        await parentWindow.HomeTab.UpdateVersionsList();
        
        
        await MessageBoxManager.GetMessageBoxStandard("Done!", "KSP2 Redux Successfully Uninstalled").ShowAsync();
    }
}