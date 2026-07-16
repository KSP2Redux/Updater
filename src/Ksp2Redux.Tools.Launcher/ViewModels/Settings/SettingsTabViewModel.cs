using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
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
    private readonly IEnvironmentProvider _environmentProvider;
    private readonly ILogService _log;

    public ObservableCollection<Ksp2InstallRowViewModel> Installs { get; } = [];
    public bool ChannelsLoaded = false;

    public ObservableCollection<string> ValidChannels { get; } = [];

    [ObservableProperty]
    public partial Ksp2InstallRowViewModel? SelectedInstall { get; set; }

    [ObservableProperty]
    public partial bool HasSelectedInstall { get; set; }

    [ObservableProperty]
    public partial bool CanRemoveSelectedInstall { get; set; }

    [ObservableProperty]
    public partial bool VerboseLogging { get; set; }

    [ObservableProperty]
    public partial bool IsAddingInstall { get; set; }

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
        ITabNavigatorService tabNavigatorService, HomeTabViewModel homeTabViewModel, IAssemblyService assemblyService,
        IMessageBoxService messageBoxService, IEnvironmentProvider environmentProvider, ILogService log)
    {
        _fileSystem = fileSystem;
        _cacheService = cacheService;
        _tabNavigatorService = tabNavigatorService;
        _launcherConfigService = launcherConfigService;
        _ksp2InstallService = ksp2InstallService;
        _homeTabViewModel = homeTabViewModel;
        _assemblyService = assemblyService;
        _messageBoxService = messageBoxService;
        _environmentProvider = environmentProvider;
        _log = log;

        _ksp2InstallService.InstallsChanged += (_, _) => RebuildInstalls();
        _ksp2InstallService.ActiveInstallChanged += (_, _) => SyncSelectedInstall();
        RebuildInstalls();

        _suppressVerboseLoggingSave = true;
        try { VerboseLogging = _launcherConfigService.Config.VerboseLogging; }
        finally { _suppressVerboseLoggingSave = false; }
        _log.MinimumLevel = VerboseLogging ? LogLevel.Debug : LogLevel.Info;
    }

    private bool _suppressVerboseLoggingSave;

    partial void OnVerboseLoggingChanged(bool value)
    {
        _log.MinimumLevel = value ? LogLevel.Debug : LogLevel.Info;
        if (_suppressVerboseLoggingSave) return;
        _launcherConfigService.Config.VerboseLogging = value;
        _launcherConfigService.Save();
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
                Installs.Add(new Ksp2InstallRowViewModel(_fileSystem, _ksp2InstallService, entry, entry.Id == activeId));
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
        Patterns = ["KSP2_x64.exe"]
    };

    private static readonly FilePickerFileType Patch = new("KSP2 Patch File")
    {
        Patterns = ["*.patch"]
    };

    [RelayCommand]
    public async Task AddInstall()
    {
        if (IsAddingInstall) return;
        IsAddingInstall = true;
        try
        {
            IStorageFile? chosenPath;
            try
            {
                chosenPath = await DoOpenFilePickerAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to open the file picker for adding an install.", ex);
                await _messageBoxService.ShowMessageBoxAsOwnedAsync("Error!",
                    $"Couldn't open the file picker: {ex.Message}", windowStartupLocation: WindowStartupLocation.CenterOwner);
                return;
            }
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
        finally
        {
            IsAddingInstall = false;
        }
    }

    [RelayCommand]
    public async Task RemoveSelectedInstall()
    {
        if (Installs.Count <= 1) return;
        if (SelectedInstall is not { } row) return;

        var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync("Confirm",
            $"Are you sure you want to remove \"{row.Name}\"? Its name, launch arguments, and Steam settings will be lost.",
            ButtonEnum.YesNo, windowStartupLocation: WindowStartupLocation.CenterOwner);
        if (result != ButtonResult.Yes) return;

        _ksp2InstallService.RemoveInstall(row.Id);
    }

    public async Task<IStorageFile?> DoOpenFilePickerAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new InvalidOperationException("Could not access the file picker (no active window).");

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
            SuggestedStartLocation = startFolder
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

        try
        {
            _cacheService.RecursivelyRestoreCache(installDir);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to uninstall Redux from {installDir}.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Error!",
                $"Couldn't uninstall Redux: {ex.Message}",
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        _ksp2InstallService.TryLoadKsp2Install();
        await _homeTabViewModel.UpdateVersionsList();


        await _messageBoxService.ShowMessageBoxAsOwnedAsync("Done!", "KSP2 Redux Successfully Uninstalled", windowStartupLocation: WindowStartupLocation.CenterOwner);
    }

    public async Task InstallFromPatchFile()
    {
        IStorageFile? chosenPath;
        try
        {
            chosenPath = await DoOpenPatchFilePickerAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to open the file picker for a patch file.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Error!",
                $"Couldn't open the file picker: {ex.Message}", windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        if (chosenPath is null) return;

        _tabNavigatorService.GoToHome();

        await _homeTabViewModel.InstallFromPatchFile(chosenPath.Path.LocalPath);
    }


    public async Task<IStorageFile?> DoOpenPatchFilePickerAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw new InvalidOperationException("Could not access the file picker (no active window).");
        var startFolder = await provider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = "Open Patch File",
            AllowMultiple = false,
            FileTypeFilter = [Patch],
            SuggestedStartLocation = startFolder
        });

        return files?.Count >= 1 ? files[0] : null;
    }

    public async Task OpenLogsFolder()
    {
        try
        {
            var logsDir = LocalStoragePaths.GetLogsDirectory(_fileSystem, _environmentProvider);
            _fileSystem.Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo(logsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Failed to open the logs folder.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Error!",
                $"Couldn't open the logs folder: {ex.Message}", windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }

    /// <summary>
    /// Everything a bug report needs in one paste-ready block: launcher version, active install
    /// details, OS, and the tail of the current log file - so a support conversation doesn't have to
    /// start with four separate questions before diagnosis can even begin.
    /// </summary>
    public string BuildDiagnosticInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"KSP2 Redux Launcher v{LauncherVersion}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");

        if (_ksp2InstallService.ActiveEntry is { } entry)
        {
            sb.AppendLine($"Active install: {entry.Name} (channel={entry.ReleaseChannel}, launchThroughSteam={entry.LaunchThroughSteam})");
            sb.AppendLine($"Exe path: {entry.ExePath}");
            var ksp2 = _ksp2InstallService.Ksp2;
            sb.AppendLine(ksp2 is { IsValid: true }
                ? $"Detected: {ksp2.Distribution}, version {ksp2.GameVersion}"
                : "Detected: (not currently detected as valid)");
        }
        else
        {
            sb.AppendLine("Active install: (none configured)");
        }

        sb.AppendLine();
        sb.AppendLine("--- Recent log ---");
        try
        {
            if (_log.CurrentLogFilePath is { } logPath && _fileSystem.File.Exists(logPath))
            {
                var lines = _fileSystem.File.ReadAllLines(logPath);
                sb.AppendLine(string.Join(_environmentProvider.NewLine, lines.TakeLast(100)));
            }
            else
            {
                sb.AppendLine("(no log file available)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not read log file: {ex.Message})");
        }

        return sb.ToString();
    }
}
