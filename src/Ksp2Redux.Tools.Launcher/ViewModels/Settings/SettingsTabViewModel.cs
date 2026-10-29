using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Settings;

public partial class SettingsTabViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
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
    private readonly IKsp2GameUninstallService _gameUninstallService;
    private readonly IGameDataFolderService _gameDataFolderService;
    private readonly ISteamSessionService _steamSession;
    private readonly ISteamDialogService _steamDialogs;

    public ObservableCollection<Ksp2InstallRowViewModel> Installs { get; } = [];
    public bool ChannelsLoaded = false;

    public ObservableCollection<string> ValidChannels { get; } = [];
    public IReadOnlyList<PatchDownloadSource> PatchDownloadSources { get; } = Enum.GetValues<PatchDownloadSource>();

    [ObservableProperty]
    public partial Ksp2InstallRowViewModel? SelectedInstall { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSteamLaunchOptions))]
    public partial bool HasSelectedInstall { get; set; }

    /// <summary>False on macOS, where the game always starts through the launcher's Wine runtime.</summary>
    public bool IsSteamLaunchSupported { get; }

    public bool ShowSteamLaunchOptions => HasSelectedInstall && IsSteamLaunchSupported;

    [ObservableProperty]
    public partial bool CanRemoveSelectedInstall { get; set; }

    [ObservableProperty]
    public partial bool VerboseLogging { get; set; }

    [ObservableProperty]
    public partial bool ParallaxMotion { get; set; }

    [ObservableProperty]
    public partial PatchDownloadSource PatchDownloadSource { get; set; }

    [ObservableProperty]
    public partial int MaxConcurrentChunkDownloads { get; set; }

    [ObservableProperty]
    public partial bool IsAddingInstall { get; set; }

    [ObservableProperty]
    public partial bool IsRenamingInstall { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UninstallKsp2ButtonText))]
    public partial bool IsUninstallingKsp2 { get; set; }

    public string UninstallKsp2ButtonText =>
        _localization[IsUninstallingKsp2 ? "Settings.UninstallKsp2.ButtonBusy" : "Settings.UninstallKsp2.Button"];

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>
    /// True with a live session or a saved login. A saved login stays valid until Steam revokes it, and the
    /// download reconnects with it on its own.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSteamSignedIn { get; set; }

    [ObservableProperty]
    public partial string SteamStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSteamBusy { get; set; }

    /// <summary>True while a live Steam connection is up, as opposed to only a saved login.</summary>
    [ObservableProperty]
    public partial bool IsSteamConnected { get; set; }

    /// <summary>True while the launcher is reconnecting to Steam or signing in.</summary>
    [ObservableProperty]
    public partial bool IsSteamConnecting { get; set; }

    [ObservableProperty]
    public partial string SteamIndicatorTooltip { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanDownloadFromSteam { get; set; }

    [ObservableProperty]
    public partial string DownloadFromSteamTooltip { get; set; } = string.Empty;

    private bool _isResumingSteam;
    private bool _steamResumeFailed;

    partial void OnIsSteamBusyChanged(bool value) => SyncSteamStatus();


    public string LauncherVersion => _assemblyService.GetVersion()?.ToString(4) ?? "?";

    public string LauncherVersionText => _localization.Format("Settings.LauncherVersion", LauncherVersion);

    // English alone is no choice at all, so the picker stays hidden until a translation is in the build.
    public bool ShowLanguagePicker => _localization.AvailableLanguages.Count > 1;

    public ObservableCollection<LanguageOption> LanguageChoices { get; } = [];

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguage { get; set; }

    private bool _suppressLanguageChange;

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_suppressLanguageChange || value is null) return;
        _launcherConfigService.Config.Language = value.CultureName;
        _launcherConfigService.Save();
        _localization.SetLanguage(value.CultureName);
    }

    // The "Automatic" entry carries text, so the list is rebuilt whenever the language changes.
    private void RebuildLanguageChoices()
    {
        var configured = _launcherConfigService.Config.Language;
        _suppressLanguageChange = true;
        try
        {
            LanguageChoices.Clear();
            LanguageChoices.Add(new LanguageOption(string.Empty, _localization["Settings.Language.Automatic"]));
            foreach (var language in _localization.AvailableLanguages) LanguageChoices.Add(language);
            SelectedLanguage = LanguageChoices.FirstOrDefault(language =>
                string.Equals(language.CultureName, configured, StringComparison.OrdinalIgnoreCase)) ?? LanguageChoices[0];
        }
        finally
        {
            _suppressLanguageChange = false;
        }
    }

    private void OnLanguageChanged()
    {
        RebuildLanguageChoices();
        foreach (var row in Installs) row.RefreshLocalizedText();
        OnPropertyChanged(nameof(UninstallKsp2ButtonText));
        OnPropertyChanged(nameof(LauncherVersionText));
        SyncSteamStatus();
    }

    private bool _suppressActiveSync;

    public void SetLoaded()
    {
        ChannelsLoaded = true;
    }

    partial void OnSelectedInstallChanged(Ksp2InstallRowViewModel? value)
    {
        IsRenamingInstall = false;
        HasSelectedInstall = value is not null;
        CanRemoveSelectedInstall = value is not null;
        if (_suppressActiveSync) return;
        if (value is null) return;
        _ksp2InstallService.SetActiveInstall(value.Id);
        _homeTabViewModel.RefreshMainButtonState();
    }

    public SettingsTabViewModel(IFileSystem fileSystem, ICacheService cacheService, ILauncherConfigService launcherConfigService,
        IKsp2InstallService ksp2InstallService,
        ITabNavigatorService tabNavigatorService, HomeTabViewModel homeTabViewModel, IAssemblyService assemblyService,
        IMessageBoxService messageBoxService, IEnvironmentProvider environmentProvider, ILogService log,
        IGameDataFolderService gameDataFolderService, ISteamSessionService steamSession, ISteamDialogService steamDialogs,
        IOperatingSystemService operatingSystemService, IKsp2GameUninstallService gameUninstallService,
        ILocalizationService localization)
    {
        _localization = localization;
        _gameUninstallService = gameUninstallService;
        IsSteamLaunchSupported = !operatingSystemService.IsMacOS();
        _steamSession = steamSession;
        _steamDialogs = steamDialogs;
        _steamSession.SignInChanged += (_, _) => Dispatcher.UIThread.Post(SyncSteamStatus);
        SyncSteamStatus();
        _fileSystem = fileSystem;
        _gameDataFolderService = gameDataFolderService;
        _cacheService = cacheService;
        _tabNavigatorService = tabNavigatorService;
        _launcherConfigService = launcherConfigService;
        _ksp2InstallService = ksp2InstallService;
        _homeTabViewModel = homeTabViewModel;
        _assemblyService = assemblyService;
        _messageBoxService = messageBoxService;
        _environmentProvider = environmentProvider;
        _log = log;
        _launcherConfigService.ConfigSaved += () => Dispatcher.UIThread.Post(SyncDownloadSettings);

        _ksp2InstallService.InstallsChanged += (_, _) => RebuildInstalls();
        _ksp2InstallService.ActiveInstallChanged += (_, _) => SyncSelectedInstall();
        RebuildInstalls();

        _suppressSettingsSave = true;
        try
        {
            VerboseLogging = _launcherConfigService.Config.VerboseLogging;
            ParallaxMotion = _launcherConfigService.Config.ParallaxMotion;
            PatchDownloadSource = _launcherConfigService.Config.PatchDownloadSource;
            MaxConcurrentChunkDownloads = _launcherConfigService.Config.MaxConcurrentChunkDownloads;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
        _log.MinimumLevel = VerboseLogging ? LogLevel.Debug : LogLevel.Info;

        RebuildLanguageChoices();
        _localization.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    private void SyncDownloadSettings()
    {
        _suppressSettingsSave = true;
        try
        {
            PatchDownloadSource = _launcherConfigService.Config.PatchDownloadSource;
            MaxConcurrentChunkDownloads = _launcherConfigService.Config.MaxConcurrentChunkDownloads;
            ParallaxMotion = _launcherConfigService.Config.ParallaxMotion;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private bool _suppressSettingsSave;

    partial void OnVerboseLoggingChanged(bool value)
    {
        _log.MinimumLevel = value ? LogLevel.Debug : LogLevel.Info;
        if (_suppressSettingsSave) return;
        _launcherConfigService.Config.VerboseLogging = value;
        _launcherConfigService.Save();
    }

    partial void OnParallaxMotionChanged(bool value)
    {
        if (_suppressSettingsSave) return;
        _launcherConfigService.Config.ParallaxMotion = value;
        _launcherConfigService.Save();
    }

    partial void OnPatchDownloadSourceChanged(PatchDownloadSource value)
    {
        if (_suppressSettingsSave) return;
        _launcherConfigService.Config.PatchDownloadSource = value;
        _launcherConfigService.Save();
    }

    partial void OnMaxConcurrentChunkDownloadsChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 8);
        if (clamped != value)
        {
            MaxConcurrentChunkDownloads = clamped;
            return;
        }
        if (_suppressSettingsSave) return;
        _launcherConfigService.Config.MaxConcurrentChunkDownloads = clamped;
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
                Installs.Add(new Ksp2InstallRowViewModel(_fileSystem, _ksp2InstallService, _messageBoxService, _localization, entry, entry.Id == activeId));
            }
        }
        SyncSelectedInstall();
    }

    private void SyncSelectedInstall()
    {
        var activeId = _ksp2InstallService.ActiveEntry?.Id;
        var match = activeId is null ? null : Installs.FirstOrDefault(r => r.Id == activeId);
        if (activeId is not null && match is null)
        {
            _log.Warn($"Active install {activeId} is missing from the Settings install list ({Installs.Count} rows, UI thread: {Dispatcher.UIThread.CheckAccess()}).");
        }
        foreach (var row in Installs)
        {
            var shouldBeActive = row.Id == activeId;
            if (row.IsActive != shouldBeActive) row.IsActive = shouldBeActive;
        }
        CanRemoveSelectedInstall = match is not null;
        if (ReferenceEquals(SelectedInstall, match)) return;
        _suppressActiveSync = true;
        try { SelectedInstall = match; }
        finally { _suppressActiveSync = false; }
    }

    private const string STEAM_INSTALL_DIR = "C:/Program Files (x86)/Steam/steamapps/common/Kerbal Space Program 2/KSP2_x64.exe";

    private FilePickerFileType Ksp2Exe => new(_localization["Settings.FilePicker.Ksp2Exe.FileType"])
    {
        Patterns = ["KSP2_x64.exe"]
    };

    private FilePickerFileType Patch => new(_localization["Settings.FilePicker.Patch.FileType"])
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
                await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                    _localization.Format("Settings.FilePicker.Failed", ex.UserMessage()), windowStartupLocation: WindowStartupLocation.CenterOwner);
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
    public void BeginRenameInstall()
    {
        if (SelectedInstall is not { } row) return;
        RenameText = row.Name;
        IsRenamingInstall = true;
    }

    /// <summary>
    /// Applies the typed name to the selected profile. A blank name keeps the old one.
    /// </summary>
    [RelayCommand]
    public void CommitRenameInstall()
    {
        if (!IsRenamingInstall) return;
        IsRenamingInstall = false;

        var name = RenameText.Trim();
        if (name.Length == 0 || SelectedInstall is not { } row || name == row.Name) return;
        row.Name = name;
    }

    [RelayCommand]
    public void CancelRenameInstall() => IsRenamingInstall = false;

    [RelayCommand]
    public async Task BrowseExePath()
    {
        if (SelectedInstall is not { } row) return;

        IStorageFile? chosen;
        try
        {
            chosen = await DoOpenFilePickerAsync(row.ExePath);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to open the file picker for an install path.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.FilePicker.Failed", ex.UserMessage()), windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        if (chosen is null) return;
        row.ExePath = chosen.Path.LocalPath;
    }

    [RelayCommand]
    public async Task SignInWithSteam()
    {
        if (IsSteamBusy) return;
        IsSteamBusy = true;
        try
        {
            if (!await _steamSession.TryResumeAsync(CancellationToken.None))
            {
                await _steamDialogs.ShowSignInAsync();
            }
        }
        finally
        {
            IsSteamBusy = false;
            SyncSteamStatus();
        }
    }

    [RelayCommand]
    public async Task SignOutOfSteam()
    {
        var revoked = await _steamSession.SignOutAsync();
        SyncSteamStatus();
        if (revoked) return;

        await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Steam.SignedOut.Title"],
            SteamSessionService.RevokeFailedMessage, icon: Icon.Warning, windowStartupLocation: WindowStartupLocation.CenterOwner);
    }

    [RelayCommand]
    public async Task DownloadFromSteam()
    {
        if (IsSteamBusy) return;
        IsSteamBusy = true;
        try
        {
            var exePath = await _steamDialogs.DownloadGameAsync();
            if (exePath is null) return;

            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Steam.DownloadComplete.Title"],
                _localization["Steam.DownloadComplete.Message"],
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            _tabNavigatorService.GoToHome();
        }
        catch (Exception ex)
        {
            _log.Error("Downloading KSP2 from Steam failed.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Steam.DownloadFailed.Title"], ex.UserMessage(),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
        finally
        {
            IsSteamBusy = false;
            SyncSteamStatus();
        }
    }

    /// <summary>Reconnects the saved Steam login, if any. Does nothing while connected or already reconnecting.</summary>
    public async Task ResumeSteamSessionAsync()
    {
        if (_steamSession.IsSignedIn || !_steamSession.HasSavedLogin || _isResumingSteam) return;

        _isResumingSteam = true;
        SyncSteamStatus();
        try
        {
            // TryResumeAsync forgets a revoked login, so one still saved after a failure is only offline.
            var resumed = await _steamSession.TryResumeAsync(CancellationToken.None);
            _steamResumeFailed = !resumed && _steamSession.HasSavedLogin;
        }
        catch (Exception ex)
        {
            _log.Warn($"Couldn't reconnect to Steam at startup: {ex.Message}");
            _steamResumeFailed = true;
        }
        finally
        {
            _isResumingSteam = false;
            SyncSteamStatus();
        }
    }

    /// <summary>Reconnects a saved Steam login, or opens the sign-in window when there is none.</summary>
    [RelayCommand]
    public async Task ConnectSteam()
    {
        if (_steamSession.IsSignedIn || _isResumingSteam || IsSteamBusy) return;

        if (_steamSession.HasSavedLogin)
        {
            await ResumeSteamSessionAsync();
        }
        else
        {
            await SignInWithSteam();
        }
    }

    private void SyncSteamStatus()
    {
        var savedAccount = _steamSession.SavedAccountName;
        IsSteamSignedIn = _steamSession.IsSignedIn || savedAccount is not null;
        SteamStatus = _steamSession.Account is { } account
            ? _localization.Format("Steam.Status.SignedIn", account.DisplayName)
            : savedAccount is null
                ? _localization["Steam.Status.NotSignedIn"]
                : _isResumingSteam
                    ? _localization.Format("Steam.Status.SignedInConnecting", savedAccount)
                    : _steamResumeFailed
                        ? _localization.Format("Steam.Status.SignedInOffline", savedAccount)
                        : _localization.Format("Steam.Status.SignedIn", savedAccount);

        IsSteamConnected = _steamSession.IsSignedIn;
        IsSteamConnecting = !IsSteamConnected && (_isResumingSteam || IsSteamBusy);
        CanDownloadFromSteam = IsSteamSignedIn && !IsSteamBusy;
        DownloadFromSteamTooltip = _localization[IsSteamSignedIn ? "Settings.DownloadFromSteam.Tooltip" : "Settings.DownloadFromSteam.SignInTooltip"];
        SteamIndicatorTooltip = IsSteamConnected ? _localization.Format("Steam.Indicator.Connected", SteamStatus)
            : IsSteamConnecting ? _localization["Steam.Indicator.Connecting"]
            : savedAccount is not null ? _localization.Format("Steam.Indicator.Reconnect", SteamStatus)
            : _localization["Steam.Indicator.NotSignedIn"];
    }

    [RelayCommand]
    public async Task RemoveSelectedInstall()
    {
        if (SelectedInstall is not { } row) return;

        var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Confirm.Title"],
            _localization.Format("Settings.RemoveProfile.Message", row.Name),
            ButtonEnum.YesNo, windowStartupLocation: WindowStartupLocation.CenterOwner);
        if (result != ButtonResult.Yes) return;

        _ksp2InstallService.RemoveInstall(row.Id);
    }

    [RelayCommand]
    public async Task OpenInstallFolder()
    {
        var exePath = _ksp2InstallService.ActiveEntry?.ExePath;
        var folder = string.IsNullOrWhiteSpace(exePath) ? null : _fileSystem.Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(folder) || !_fileSystem.Directory.Exists(folder))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization["Settings.OpenInstallFolder.NotFound"],
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.OpenInstallFolder.Failed", e.Message),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }

    /// <summary>
    /// Opens the folder KSP2 keeps its saves, settings and its own log file in.
    /// </summary>
    // Unity only creates this the first time the game runs, so a path that resolves but is not there
    // yet is a different problem from one that cannot be worked out at all, and says so.
    [RelayCommand]
    public async Task OpenGameDataFolder()
    {
        var folder = _gameDataFolderService.Resolve(_ksp2InstallService.ActiveEntry);

        if (string.IsNullOrWhiteSpace(folder))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization["Settings.GameData.Unknown"],
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        if (!_fileSystem.Directory.Exists(folder))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Settings.GameData.NotYet.Title"],
                _localization.Format("Settings.GameData.NotYet.Message", folder),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log.Error($"Failed to open the game data folder at {folder}.", e);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.GameData.OpenFailed", e.Message),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }

    public async Task<IStorageFile?> DoOpenFilePickerAsync(string? startNear = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } provider)
            throw _localization.Message("Settings.FilePicker.NoWindow").ToException(message => new InvalidOperationException(message));

        IStorageFolder? startFolder = null;
        var lastKnownPath = startNear ?? _ksp2InstallService.ActiveEntry?.ExePath;
        var lastKnownFolder = string.IsNullOrWhiteSpace(lastKnownPath) ? null : _fileSystem.Path.GetDirectoryName(lastKnownPath);
        if (!string.IsNullOrEmpty(lastKnownFolder) && _fileSystem.Directory.Exists(lastKnownFolder))
        {
            startFolder = await provider.TryGetFolderFromPathAsync(lastKnownFolder);
        }
        startFolder ??= await provider.TryGetFolderFromPathAsync(STEAM_INSTALL_DIR);
        startFolder ??= await provider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop);

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = _localization["Settings.FilePicker.Ksp2Exe.Title"],
            AllowMultiple = false,
            FileTypeFilter = [Ksp2Exe],
            SuggestedStartLocation = startFolder
        });

        return files?.Count >= 1 ? files[0] : null;
    }

    /// <summary>
    /// Removes the selected profile's copy of KSP2 from the computer, then the profile itself.
    /// </summary>
    [RelayCommand]
    public async Task UninstallKsp2()
    {
        if (IsUninstallingKsp2 || SelectedInstall is not { } row) return;

        var removal = _gameUninstallService.Inspect(row.ExePath);
        switch (removal.Kind)
        {
            case Ksp2GameRemovalKind.NotAGameFolder:
                await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Settings.UninstallKsp2.NotAGameFolder.Title"],
                    _localization.Format("Settings.UninstallKsp2.NotAGameFolder.Message", removal.Folder, Ksp2Install.KSP2_EXE_NAME),
                    icon: Icon.Warning, windowStartupLocation: WindowStartupLocation.CenterOwner);
                return;

            case Ksp2GameRemovalKind.Missing:
                if (await ConfirmAsync(_localization["Settings.UninstallKsp2.Missing.Title"],
                        _localization.Format("Settings.UninstallKsp2.Missing.Message", row.Name)))
                {
                    _ksp2InstallService.RemoveInstall(row.Id);
                }
                return;

            case Ksp2GameRemovalKind.UninstallThroughSteam:
                if (!await ConfirmAsync(_localization["Settings.UninstallKsp2.Confirm.Title"],
                        _localization.Format("Settings.UninstallKsp2.ThroughSteam.Message", row.Name)))
                {
                    return;
                }
                try
                {
                    Process.Start(new ProcessStartInfo("steam://uninstall/954850") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _log.Error("Failed to hand the KSP2 uninstall to Steam.", ex);
                    await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                        _localization.Format("Settings.UninstallKsp2.SteamFailed", ex.Message), windowStartupLocation: WindowStartupLocation.CenterOwner);
                    return;
                }
                _ksp2InstallService.RemoveInstall(row.Id);
                return;
        }

        if (!await ConfirmAsync(_localization["Settings.UninstallKsp2.Confirm.Title"],
                _localization.Format("Settings.UninstallKsp2.Confirm.Message", removal.Folder, row.Name)))
        {
            return;
        }

        IsUninstallingKsp2 = true;
        try
        {
            await _gameUninstallService.DeleteAsync(removal);
            _ksp2InstallService.RemoveInstall(row.Id);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to delete the KSP2 install at {removal.Folder}.", ex);
            _ksp2InstallService.TryLoadKsp2Install();
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.UninstallKsp2.Failed", ex.UserMessage()),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }
        finally
        {
            IsUninstallingKsp2 = false;
        }

        await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Done.Title"],
            _localization["Settings.UninstallKsp2.Done"],
            windowStartupLocation: WindowStartupLocation.CenterOwner);
    }

    private async Task<bool> ConfirmAsync(string title, string text) =>
        await _messageBoxService.ShowMessageBoxAsOwnedAsync(title, text, ButtonEnum.YesNo, Icon.Warning,
            windowStartupLocation: WindowStartupLocation.CenterOwner) == ButtonResult.Yes;

    public async Task UninstallRedux()
    {
        var activeExe = _ksp2InstallService.ActiveEntry?.ExePath;
        if (string.IsNullOrWhiteSpace(activeExe))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization["Settings.UninstallRedux.NoInstall"],
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }
        var installDir = _fileSystem.Path.GetDirectoryName(activeExe);
        if (string.IsNullOrEmpty(installDir) || !_fileSystem.File.Exists(_fileSystem.Path.Combine(installDir, "uninstall.zip")))
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization["Settings.UninstallRedux.NotInstalled"], windowStartupLocation:WindowStartupLocation.CenterOwner);
            return;
        }

        var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Confirm.Title"],
            _localization["Settings.UninstallRedux.Confirm"],
            ButtonEnum.YesNo, windowStartupLocation:WindowStartupLocation.CenterOwner);
        if (result != ButtonResult.Yes) return;

        try
        {
            _cacheService.RecursivelyRestoreCache(installDir);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to uninstall Redux from {installDir}.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.UninstallRedux.Failed", ex.UserMessage()),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        _ksp2InstallService.TryLoadKsp2Install();
        await _homeTabViewModel.UpdateVersionsList();


        await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Done.Title"],
            _localization["Settings.UninstallRedux.Done"], windowStartupLocation: WindowStartupLocation.CenterOwner);
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
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.FilePicker.Failed", ex.UserMessage()), windowStartupLocation: WindowStartupLocation.CenterOwner);
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
            throw _localization.Message("Settings.FilePicker.NoWindow").ToException(message => new InvalidOperationException(message));
        var startFolder = await provider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = _localization["Settings.FilePicker.Patch.Title"],
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
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.LogsFolder.Failed", ex.Message), windowStartupLocation: WindowStartupLocation.CenterOwner);
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
        sb.AppendLine($"Patch downloads: source={PatchDownloadSource}, concurrency={MaxConcurrentChunkDownloads}");

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
