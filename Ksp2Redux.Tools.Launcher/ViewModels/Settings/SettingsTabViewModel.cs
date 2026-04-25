using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System;
using System.Collections.ObjectModel;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Settings;

public partial class SettingsTabViewModel : ViewModelBase
{
    private readonly IFileSystem _fileSystem;
    private readonly ICacheService _cacheService;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IKsp2InstallService _ksp2InstallService;
    private readonly ITabNavigatorService _tabNavigatorService;
    private readonly HomeTabViewModel _homeTabViewModel;
    
    
    public string DisplayedInstallPath => _launcherConfigService.Config.Ksp2InstallPath;
    public bool ChannelsLoaded = false;
    
    public string ReleaseChannel
    {
        get => ChannelsLoaded ? _launcherConfigService.Config.ReleaseChannel : "";
        set
        {
            if (!ChannelsLoaded) return;
            _launcherConfigService.Config.ReleaseChannel = value;
            _launcherConfigService.Save();
            _ = _homeTabViewModel.UpdateVersionsList();
        }
    }

    public ObservableCollection<string> ValidChannels { get; } = [];

    public bool LaunchThroughSteam
    {
        get => _launcherConfigService.Config.LaunchThroughSteam;
        set
        {
            if (_launcherConfigService.Config.LaunchThroughSteam == value) return;
            _launcherConfigService.Config.LaunchThroughSteam = value;
            _launcherConfigService.Save();
            OnPropertyChanged();
            _homeTabViewModel.RefreshMainButtonState();
        }
    }

    public string SteamAppId
    {
        get => _launcherConfigService.Config.SteamAppId;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (_launcherConfigService.Config.SteamAppId == normalized) return;
            _launcherConfigService.Config.SteamAppId = normalized;
            _launcherConfigService.Save();
            OnPropertyChanged();
        }
    }

    public string LaunchArguments
    {
        get => _launcherConfigService.Config.LaunchArguments;
        set
        {
            var normalized = value ?? "";
            if (_launcherConfigService.Config.LaunchArguments == normalized) return;
            _launcherConfigService.Config.LaunchArguments = normalized;
            _launcherConfigService.Save();
            OnPropertyChanged();
        }
    }

    public void SetLoaded()
    {
        ChannelsLoaded = true;
        ReleaseChannel = _launcherConfigService.Config.ReleaseChannel;
        OnPropertyChanged(nameof(ReleaseChannel));
    }
    
    public SettingsTabViewModel(IFileSystem fileSystem, ICacheService cacheService, ILauncherConfigService launcherConfigService, IKsp2InstallService ksp2InstallService,
        ITabNavigatorService tabNavigatorService, HomeTabViewModel homeTabViewModel)
    {
        _fileSystem = fileSystem;
        _cacheService = cacheService;
        _tabNavigatorService = tabNavigatorService;
        _launcherConfigService = launcherConfigService;
        _ksp2InstallService = ksp2InstallService;
        _homeTabViewModel = homeTabViewModel;
    }

    private const string STEAM_INSTALL_DIR = "C:/Program Files (x86)/Steam/steamapps/common/Kerbal Space Program 2/KSP2_x64.exe";

    private static readonly FilePickerFileType Ksp2Exe = new("KSP2 installation")
    {
        Patterns = ["KSP2_x64.exe"],
    };
    
    private static readonly FilePickerFileType Patch = new("KSP2 Patch File")
    {
        Patterns = ["*.patch"],
    };

    public async Task SelectGameInstallDirectory()
    {

        var chosenPath = await DoOpenFilePickerAsync();
        if (chosenPath is not null)
        {
            _launcherConfigService.Config.Ksp2InstallPath = chosenPath.Path.LocalPath;
            _launcherConfigService.Save();
            // TODO: trigger update patch status
        }
        _ksp2InstallService.TryLoadKsp2Install();
        //return config.Ksp2InstallPath;
    }

    public async Task<IStorageFile?> DoOpenFilePickerAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");

        IStorageFolder? startFolder = null;
        // default to previously select install path if it exists.
        if (!string.IsNullOrWhiteSpace(_launcherConfigService.Config.Ksp2InstallPath) && _fileSystem.Path.Exists(_launcherConfigService.Config.Ksp2InstallPath))
        {
            startFolder = await provider.TryGetFolderFromPathAsync(_launcherConfigService.Config.Ksp2InstallPath);
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
        // parentWindow.TryLoadKsp2Install();
        var installDir = _fileSystem.Path.GetDirectoryName(_launcherConfigService.Config.Ksp2InstallPath);;
        if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(installDir, "uninstall.zip")))
        {
            await MessageBoxManager.GetMessageBoxStandard("Error!", "Redux is not installed...", windowStartupLocation:WindowStartupLocation.CenterOwner).ShowAsOwnedAsync();
            return;
        }

        var box = MessageBoxManager.GetMessageBoxStandard("Confirm", "Are you sure you want to uninstall Redux?",
            ButtonEnum.YesNo, windowStartupLocation:WindowStartupLocation.CenterOwner);


        var result = await box.ShowAsOwnedAsync();
        if (result != ButtonResult.Yes) return;
        
        _cacheService.RecursivelyRestoreCache(installDir);
        
        _ksp2InstallService.TryLoadKsp2Install();
        await _homeTabViewModel.UpdateVersionsList();
        
        
        await MessageBoxManager.GetMessageBoxStandard("Done!", "KSP2 Redux Successfully Uninstalled", windowStartupLocation: WindowStartupLocation.CenterOwner).ShowAsOwnedAsync();
    }

    public async Task InstallFromPatchFile()
    {
        var chosenPath = await DoOpenPatchFilePickerAsync();

        if (chosenPath is null) return;
        
        _tabNavigatorService.GoToHome();
        
        await _homeTabViewModel.InstallFromPatchFile(chosenPath.Path.LocalPath);
    }


    public async Task<IStorageFile?> DoOpenPatchFilePickerAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");
        var startFolder = await provider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
        
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = "Open Patch File",
            AllowMultiple = false,
            FileTypeFilter = [Patch],
            SuggestedStartLocation = startFolder,
        });
        
        return files?.Count >= 1 ? files[0] : null;
    }

    public void UpdateInstallPath()
    {
        OnPropertyChanged(nameof(DisplayedInstallPath));
    }
}