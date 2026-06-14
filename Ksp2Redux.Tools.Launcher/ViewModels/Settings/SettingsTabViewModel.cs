using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System;
using System.Collections.ObjectModel;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IAssemblyService _assemblyService;
    private readonly IMessageBoxService _messageBoxService;

    public ObservableCollection<Ksp2InstallRowViewModel> Installs { get; } = [];
    public bool ChannelsLoaded = false;

    public ObservableCollection<string> ValidChannels { get; } = [];

    [ObservableProperty] private Ksp2InstallRowViewModel? _selectedInstall;
    [ObservableProperty] private bool _hasSelectedInstall;
    [ObservableProperty] private bool _canRemoveSelectedInstall;

    public string LauncherVersion => _assemblyService.GetVersion()?.ToString(4) ?? "?";

    private bool _suppressActiveSync;

    public void SetLoaded()
    {
        ChannelsLoaded = true;
    }

    partial void OnSelectedInstallChanged(Ksp2InstallRowViewModel? value)
    {
        HasSelectedInstall = value is not null;
        CanRemoveSelectedInstall = value is not null && Installs.Count > 1;
        if (_suppressActiveSync) return;
        if (value is null) return;
        _ksp2InstallService.SetActiveInstall(value.Id);
        _homeTabViewModel.RefreshMainButtonState();
    }

    public SettingsTabViewModel(IFileSystem fileSystem, ICacheService cacheService, ILauncherConfigService launcherConfigService,
        IKsp2InstallService ksp2InstallService,
        ITabNavigatorService tabNavigatorService, HomeTabViewModel homeTabViewModel, IAssemblyService assemblyService, IMessageBoxService messageBoxService)
    {
        _fileSystem = fileSystem;
        _cacheService = cacheService;
        _tabNavigatorService = tabNavigatorService;
        _launcherConfigService = launcherConfigService;
        _ksp2InstallService = ksp2InstallService;
        _homeTabViewModel = homeTabViewModel;
        _assemblyService = assemblyService;
        _messageBoxService = messageBoxService;

        _ksp2InstallService.InstallsChanged += (_, _) => RebuildInstalls();
        _ksp2InstallService.ActiveInstallChanged += (_, _) => SyncSelectedInstall();
        RebuildInstalls();
    }

    private void RebuildInstalls()
    {
        var activeId = _ksp2InstallService.ActiveEntry?.Id;
        var entries = _ksp2InstallService.Entries;

        // Keep existing row VMs when IDs match, otherwise focus is lost on bound TextBoxes.
        var structuralChange = Installs.Count != entries.Count;
        if (!structuralChange)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (Installs[i].Id != entries[i].Id) { structuralChange = true; break; }
            }
        }

        if (structuralChange)
        {
            Installs.Clear();
            foreach (var entry in entries)
            {
                Installs.Add(new Ksp2InstallRowViewModel(_ksp2InstallService, entry, entry.Id == activeId));
            }
        }
        SyncSelectedInstall();
    }

    private void SyncSelectedInstall()
    {
        var activeId = _ksp2InstallService.ActiveEntry?.Id;
        var match = activeId is null ? null : Installs.FirstOrDefault(r => r.Id == activeId);
        foreach (var row in Installs)
        {
            var shouldBeActive = row.Id == activeId;
            if (row.IsActive != shouldBeActive) row.IsActive = shouldBeActive;
        }
        CanRemoveSelectedInstall = match is not null && Installs.Count > 1;
        if (ReferenceEquals(SelectedInstall, match)) return;
        _suppressActiveSync = true;
        try { SelectedInstall = match; }
        finally { _suppressActiveSync = false; }
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

    [RelayCommand]
    public async Task AddInstall()
    {
        var chosenPath = await DoOpenFilePickerAsync();
        if (chosenPath is null) return;

        var path = chosenPath.Path.LocalPath;
        var existing = _ksp2InstallService.Entries.FirstOrDefault(e =>
            string.Equals(e.ExePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _ksp2InstallService.SetActiveInstall(existing.Id);
            return;
        }
        _ksp2InstallService.AddInstall(path);
    }

    [RelayCommand]
    public void RemoveSelectedInstall()
    {
        if (Installs.Count <= 1) return;
        if (SelectedInstall is { } row) _ksp2InstallService.RemoveInstall(row.Id);
    }

    public async Task<IStorageFile?> DoOpenFilePickerAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new NullReferenceException("Missing StorageProvider instance.");

        IStorageFolder? startFolder = null;
        var lastKnownPath = _ksp2InstallService.ActiveEntry?.ExePath;
        if (!string.IsNullOrWhiteSpace(lastKnownPath) && _fileSystem.Path.Exists(lastKnownPath))
        {
            startFolder = await provider.TryGetFolderFromPathAsync(lastKnownPath);
        }
        startFolder ??= await provider.TryGetFolderFromPathAsync(STEAM_INSTALL_DIR);
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
        var activeExe = _ksp2InstallService.ActiveEntry?.ExePath;
        if (string.IsNullOrWhiteSpace(activeExe))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Error!", "No active KSP2 install selected.",
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }
        var installDir = _fileSystem.Path.GetDirectoryName(activeExe);
        if (string.IsNullOrEmpty(installDir) || !_fileSystem.File.Exists(_fileSystem.Path.Combine(installDir, "uninstall.zip")))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Error!", "Redux is not installed...", windowStartupLocation:WindowStartupLocation.CenterOwner);
            return;
        }

        var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync("Confirm", "Are you sure you want to uninstall Redux?",
            ButtonEnum.YesNo, windowStartupLocation:WindowStartupLocation.CenterOwner);
        if (result != ButtonResult.Yes) return;

        _cacheService.RecursivelyRestoreCache(installDir);

        _ksp2InstallService.TryLoadKsp2Install();
        await _homeTabViewModel.UpdateVersionsList();


        await _messageBoxService.ShowMessageBoxAsOwnedAsync("Done!", "KSP2 Redux Successfully Uninstalled", windowStartupLocation: WindowStartupLocation.CenterOwner);
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
}
