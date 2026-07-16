using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public partial class HomeTabViewModel : ViewModelBase
{
    private readonly IKsp2InstallService _ksp2InstallService;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IReleasesFeedService _releasesFeedService;
    private readonly IInstallPlanService _installPlanService;
    private readonly IUpdateService _updateService;
    private readonly IOperatingSystemService _operatingSystemService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly ILogService _log;
    
    public ObservableCollection<GameVersionViewModel> Versions { get; } = [];
    public ObservableCollection<Ksp2InstallChoiceViewModel> Installs { get; } = [];

    [ObservableProperty]
    public partial GameVersionViewModel? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial Ksp2InstallChoiceViewModel? SelectedInstall { get; set; }

    [ObservableProperty]
    public partial bool IsInstallSwitcherEnabled { get; set; }

    [ObservableProperty]
    public partial bool FeedRefreshFailed { get; set; }

    public enum MainButtonState
    {
        Launch,
        Install,
        Update,
        Cancel
    }
    [ObservableProperty]
    public partial MainButtonState MainButtonShown { get; set; }

    [ObservableProperty]
    public partial bool MainButtonEnabled { get; set; }

    // Null/empty means "no tooltip" - the enabled states below intentionally clear this, since
    // a tooltip that just repeats the button's own label (e.g. "Install Ksp2Redux" on an already
    // labeled INSTALL button) is redundant clutter. It's only worth showing when it explains why
    // the button is disabled, which the label alone can't do.
    [ObservableProperty]
    public partial string? MainButtonTooltip { get; set; } = "Loading...";

    [ObservableProperty]
    public partial bool IsProgressVisible { get; set; }

    [ObservableProperty]
    public partial float DownloadProgressMb { get; set; } = 250;

    [ObservableProperty]
    public partial float DownloadProgressTotalMb { get; set; } = 450;

    [ObservableProperty]
    public partial float InstallProgressSteps { get; set; } = 1;

    [ObservableProperty]
    public partial float InstallProgressTotalSteps { get; set; } = 3;

    [ObservableProperty]
    public partial bool IsInstallLogVisible { get; set; }

    [ObservableProperty]
    public partial string InstallLogText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool InstallationDisabled { get; set; }

    private readonly StringBuilder _installLogBuilder = new();
    private readonly Lock _installLogLock = new();
    private bool _installLogUpdateQueued;
    private CancellationTokenSource? _cancelCurrentOperation;

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => (item as GameVersionViewModel)?.Channel ?? string.Empty;

    public HomeTabViewModel(IKsp2InstallService ksp2InstallService,
        ILauncherConfigService launcherConfigService, IReleasesFeedService releasesFeedService, IInstallPlanService installPlanService, IUpdateService updateService, IOperatingSystemService operatingSystemService, IMessageBoxService messageBoxService, ILogService log)
    {
        _ksp2InstallService = ksp2InstallService;
        _launcherConfigService = launcherConfigService;
        _releasesFeedService = releasesFeedService;
        _installPlanService = installPlanService;
        _updateService = updateService;
        _operatingSystemService = operatingSystemService;
        _messageBoxService = messageBoxService;
        _log = log;

        RebuildInstallsCollection();
        RebuildVersionsCollection();
        PropertyChanged += ReactToPropertyChanges;

        _ksp2InstallService.InstallsChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                RebuildInstallsCollection();
                UpdateMainButtonState();
            });
        _ksp2InstallService.ActiveInstallChanged += (_, _) =>
            Dispatcher.UIThread.Post(async () =>
            {
                SyncSelectedInstall();
                await UpdateVersionsList(false);
            });

        // Periodically re-pull the release feeds so newly published versions appear without a restart.
        var feedRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        feedRefreshTimer.Tick += (_, _) => RefreshFeedsCommand.Execute(null);
        feedRefreshTimer.Start();
    }

    private void RebuildInstallsCollection()
    {
        Installs.Clear();
        foreach (var entry in _ksp2InstallService.Entries)
        {
            Installs.Add(new Ksp2InstallChoiceViewModel(entry));
        }
        IsInstallSwitcherEnabled = Installs.Count > 1;
        SyncSelectedInstall();
    }

    private bool _suppressInstallSelectionChange;
    private bool _appliedLaunchVersionDefault;

    private void SyncSelectedInstall()
    {
        var activeId = _ksp2InstallService.ActiveEntry?.Id;
        var match = activeId is null ? null : Installs.FirstOrDefault(i => i.Id == activeId);
        if (ReferenceEquals(SelectedInstall, match)) return;
        _suppressInstallSelectionChange = true;
        try { SelectedInstall = match; }
        finally { _suppressInstallSelectionChange = false; }
    }


    [RelayCommand]
    public async Task UpdateVersionsList(bool updateChannels = true)
    {
        if(updateChannels)
            await UpdateAsync();
        RebuildVersionsCollectionPreservingSelection();
        UpdateMainButtonState();
    }
    private async Task UpdateAsync()
    {
        var allSucceeded = true;
        foreach (var feed in _releasesFeedService.ReleasesFeed)
        {
            if (!await feed.Value.UpdateManifest()) allSucceeded = false;
        }
        FeedRefreshFailed = !allSucceeded;
    }

    // Single refresh entry point shared by the periodic timer and, later, a manual button / F5.
    // The generated RefreshFeedsCommand is an AsyncRelayCommand, so a tick that lands while a
    // refresh is already running is ignored, and a UI control can bind its IsRunning flag.
    [RelayCommand]
    public async Task RefreshFeeds()
    {
        // Leave the versions list and main button alone while an install or update is running,
        // otherwise a background refresh would clobber the Cancel button mid operation.
        if (_cancelCurrentOperation is not null) return;

        await UpdateAsync();
        RebuildVersionsCollectionPreservingSelection();
        UpdateMainButtonState();
    }

    // RebuildVersionsCollection resets SelectedVersion to a default, which would pull the user's
    // choice out from under them any time the list gets rebuilt - a periodic feed refresh, or an
    // unrelated settings change to the active install (which also fires ActiveInstallChanged).
    // Capture and restore it around the rebuild instead.
    private void RebuildVersionsCollectionPreservingSelection()
    {
        var previous = SelectedVersion;
        RebuildVersionsCollection();
        if (previous is not null)
        {
            var match = Versions.FirstOrDefault(v =>
                v.Channel == previous.Channel && v.Version.Equals(previous.Version));
            if (match is not null) SelectedVersion = match;
        }
    }

    [RelayCommand]
    public async Task UpdateRedux()
    {
        await RunPatchProcess();
    }

    [RelayCommand]
    public async Task InstallRedux()
    {
        await RunPatchProcess();
    }

    [RelayCommand]
    public async Task LaunchGame()
    {
        // Re-validate right before using it rather than trusting whatever was cached at the last
        // refresh - the install could have been moved, had its drive ejected, or been deleted in
        // the meantime if the app was left idle.
        _ksp2InstallService.TryLoadKsp2Install();
        if (_ksp2InstallService.Ksp2 is not { IsValid: true })
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Launch",
                "KSP2 installation not detected. Please select a directory containing KSP2 on the settings tab.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }
        var activeEntry = _ksp2InstallService.ActiveEntry;
        if (activeEntry is null) return;

        if (activeEntry.LaunchThroughSteam)
        {
            var appId = activeEntry.SteamAppId;
            if (string.IsNullOrWhiteSpace(appId)) appId = "954850";
            var startInfo = new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{appId}",
                UseShellExecute = true
            };
            try
            {
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to launch through Steam.", ex);
                await _messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Launch",
                    $"Couldn't open Steam: {ex.Message}\nMake sure Steam is installed and try again.",
                    ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
            }
            return;
        }

        MainButtonEnabled = false;
        try
        {
            using Process process = new();
            process.StartInfo.FileName = _ksp2InstallService.Ksp2.ExePath;
            process.StartInfo.WorkingDirectory = _ksp2InstallService.Ksp2.InstallDir;
            var launchArgs = activeEntry.LaunchArguments;
            if (!string.IsNullOrWhiteSpace(launchArgs))
            {
                process.StartInfo.Arguments = launchArgs;
            }
            process.Start();
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to launch KSP2.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Launch",
                $"Couldn't start the game: {ex.Message}\nIt may have been moved, removed, or blocked by antivirus software.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
        finally
        {
            MainButtonEnabled = true;
        }
    }

    [RelayCommand]
    public void CancelCurrentMainButtonAction()
    {
        _cancelCurrentOperation?.Cancel();
    }

    private void ReactToPropertyChanges(object? sender, PropertyChangedEventArgs? e)
    {
        if (e?.PropertyName == nameof(SelectedVersion))
        {
            UpdateMainButtonState();
        }
        else if (e?.PropertyName == nameof(SelectedInstall))
        {
            if (_suppressInstallSelectionChange) return;
            if (SelectedInstall is { } choice)
            {
                _ksp2InstallService.SetActiveInstall(choice.Id);
            }
        }
    }

    
    public void RefreshMainButtonState() => UpdateMainButtonState();

    private void UpdateMainButtonState()
    {
        _ksp2InstallService.TryLoadKsp2Install();
        var ksp2 = _ksp2InstallService.Ksp2;
        if (ksp2 is null || !ksp2.IsValid)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = "KSP2 installation not detected.  Please select a directory containing KSP2 on the settings tab.";
            return;
        }

        // IsValid only confirms the exe exists - it says nothing about whether we could actually
        // read its version. Falling through without this check let Install/Update stay enabled with
        // an unknown "from" version, which crashed or hung once clicked (see issue #26).
        if (ksp2.GameVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = "Couldn't detect the installed game version. Verifying game files through Steam/Epic, or reinstalling the game, may fix this.";
            return;
        }

        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Install;
            MainButtonTooltip = "Please select a version to install or launch.";
            return;
        }

        var linuxLaunchBlocked = _operatingSystemService.IsLinux() && !(_ksp2InstallService.ActiveEntry?.LaunchThroughSteam ?? false);
        const string linuxLaunchBlockedTooltip = "Enable \"Launch through Steam\" in settings to launch on Linux.";

        const string installationDisabledTooltip = "The launcher needs to update itself before you can install or update Redux.";

        if (ksp2.Distribution != Distribution.Redux)
        {
            MainButtonEnabled = true;
            MainButtonTooltip = null;
            if (selectedVersion.Version.Equals(ksp2.GameVersion) || selectedVersion.Channel == "installed")
            {
                MainButtonShown = MainButtonState.Launch;
                if (linuxLaunchBlocked)
                {
                    MainButtonEnabled = false;
                    MainButtonTooltip = linuxLaunchBlockedTooltip;
                }
            }
            else
            {
                MainButtonEnabled = !InstallationDisabled;
                MainButtonShown = MainButtonState.Install;
                if (InstallationDisabled) MainButtonTooltip = installationDisabledTooltip;
            }
            return;
        }

        if (selectedVersion.Version.Equals(ksp2.GameVersion) || selectedVersion.Channel == "installed")
        {
            MainButtonEnabled = true;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = null;
            if (linuxLaunchBlocked)
            {
                MainButtonEnabled = false;
                MainButtonTooltip = linuxLaunchBlockedTooltip;
            }
        }
        else
        {
            MainButtonEnabled = !InstallationDisabled;
            MainButtonShown = MainButtonState.Update;
            MainButtonTooltip = InstallationDisabled ? installationDisabledTooltip : null;
        }
    }

    private void RebuildVersionsCollection()
    {
        // We want to do this for the moment, we could fix this logic later at some point
        _ksp2InstallService.TryLoadKsp2Install();
        
        Versions.Clear();
        
        GameVersionViewModel? currentVersion = null;
        if (_ksp2InstallService.Ksp2?.GameVersion != null)
        {
            currentVersion = new GameVersionViewModel(_ksp2InstallService.Ksp2.GameVersion)
            {
                Channel = "installed"
            };
            Versions.Add(currentVersion);
        }
        
        
        var activeChannel = _ksp2InstallService.ActiveEntry?.ReleaseChannel;
        if (string.IsNullOrEmpty(activeChannel)
            || !_releasesFeedService.ReleasesFeed.TryGetValue(activeChannel, out var value))
        {
            return;
        }
        // Select the correct version with the release view
        foreach (var releaseView in value.GetAllVersions().Select(gv => new GameVersionViewModel(gv)))
        {
            if (Versions.All(x => !(x.Version.Equals(releaseView.Version) && x.Channel.Equals(releaseView.Channel))))
            {
                Versions.Add(releaseView);
            }
        }

        GameVersionViewModel? defaultVersion = currentVersion;
        if (!_appliedLaunchVersionDefault)
        {
            _appliedLaunchVersionDefault = true;
            var latest = value.GetLatestVersion();
            var installed = _ksp2InstallService.Ksp2?.GameVersion;
            if (latest is not null && (installed is null || !latest.Equals(installed)))
            {
                defaultVersion = Versions.FirstOrDefault(v =>
                    v.Channel == activeChannel && v.Version.Equals(latest)) ?? currentVersion;
            }
        }
        SelectedVersion = defaultVersion;
    }

    private async Task RunPatchProcess()
    {
        // // lock main window tabs?
        
        ResetInstallLog();
        IsInstallLogVisible = true;
        IsProgressVisible = true;
        DownloadProgressMb = 0;
        DownloadProgressTotalMb = 0;
        InstallProgressSteps = 0;
        InstallProgressTotalSteps = 1;
        
        // gather process dependencies.
        
        _ksp2InstallService.TryLoadKsp2Install();
        var ksp2 = _ksp2InstallService.Ksp2;
        if (ksp2 is null || SelectedVersion is null)
        {
            return;
        }

        // The main button should already be disabled whenever this is null (see
        // UpdateMainButtonState), but guard here too rather than pass a null "from" version into
        // GetPatchListToVersion, which assumes a real GameVersion and would throw or search a
        // patch graph that was never meant to be reached from "unknown".
        if (ksp2.GameVersion is null)
        {
            Log("Couldn't detect the installed game version, so it's unclear which patches to apply. Verifying game files, or reinstalling the game, may fix this.");
            return;
        }

        // Set up process cancellation trigger.
        MainButtonShown = MainButtonState.Cancel;
        MainButtonEnabled = true;
        MainButtonTooltip = null;
        _cancelCurrentOperation = new CancellationTokenSource();

        Log("Creating install plan");

        var plan = _releasesFeedService.ReleasesFeed[SelectedVersion.Channel]
            .GetPatchListToVersion(ksp2.GameVersion, SelectedVersion.Version);
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log("KSP2 Redux Successfully Installed");
        }
        catch (OperationCanceledException)
        {
            Log("Installation cancelled");
        }
        catch (InstallFailedException e)
        {
            LogError(e.Message, e);
        }
        catch (Exception e)
        {
            LogError($"Error updating Redux: {e.Message}", e);
            Log("Redux may be in an invalid state, try uninstalling and reinstalling");
        }
        finally
        {
            _cancelCurrentOperation = null;
            await FlushPendingLogWrites();
            IsProgressVisible = false;
            _ksp2InstallService.TryLoadKsp2Install();
            await UpdateVersionsList();
        }
    }

    public async Task InstallFromPatchFile(string path)
    {
        
        ResetInstallLog();
        IsInstallLogVisible = true;
        IsProgressVisible = true;
        DownloadProgressMb = 0;
        DownloadProgressTotalMb = 0;
        InstallProgressSteps = 0;
        InstallProgressTotalSteps = 1;
        
        var plan = new InstallPlan();
        plan.ApplyPatchFile(path);
        plan.Prepatch();
        plan.RevertToStock();
        
        _ksp2InstallService.TryLoadKsp2Install();
        var ksp2 = _ksp2InstallService.Ksp2;
        if (ksp2 is null || SelectedVersion is null)
        {
            return;
        }
        
        MainButtonShown = MainButtonState.Cancel;
        MainButtonEnabled = true;
        MainButtonTooltip = null;
        _cancelCurrentOperation = new CancellationTokenSource();
        
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log("KSP2 Redux Successfully Installed");
        }
        catch (OperationCanceledException)
        {
            Log("Installation cancelled");
        }
        catch (InstallFailedException e)
        {
            LogError(e.Message, e);
        }
        catch (Exception e)
        {
            LogError($"Error updating Redux: {e.Message}", e);
            Log("Redux may be in an invalid state, try uninstalling and reinstalling");
        }
        finally
        {
            _cancelCurrentOperation = null;
            await FlushPendingLogWrites();
            IsProgressVisible = false;
            _ksp2InstallService.TryLoadKsp2Install();
            await UpdateVersionsList();
        }
    }


    private async Task RunPlanOnInstall(InstallPlan plan, Ksp2Install ksp2)
    {
        UpdateStepsProgress(0, plan.Steps.Count);
        _installPlanService.Describe(plan, Log);

        // TODO: Could this be replaced by Dispatcher.UIThread.Post?
        await Task.Run(
            () => _installPlanService.ApplyToFolder(
                plan,
                ksp2.InstallDir,
                Log,
                UpdateDownloadProgress,
                UpdateStepsProgress,
                _cancelCurrentOperation!.Token),
            _cancelCurrentOperation!.Token);

        void UpdateStepsProgress(int current, int max)
        {
            Dispatcher.UIThread.Post(() =>
            {
                InstallProgressSteps = current;
                InstallProgressTotalSteps = max;
            });
        }

        void UpdateDownloadProgress(long value, long max)
        {
            Dispatcher.UIThread.Post(() =>
            {
                DownloadProgressMb = value / 1024f / 1024f;
                DownloadProgressTotalMb = max / 1024f / 1024f;
            });
        }
    }

    private void Log(string message)
    {
        _log.Info(message);
        AppendToInstallLog(message);
    }

    // Real install/patch failures used to go through Log(), which always writes at Info - grepping a
    // user's log file for "ERROR" to find out what went wrong turned up nothing, since the failure and
    // its stack trace were filed alongside routine progress lines.
    private void LogError(string message, Exception? exception = null)
    {
        _log.Error(message, exception);
        AppendToInstallLog(message);
    }

    private void AppendToInstallLog(string message)
    {
        bool queueUpdate;
        lock (_installLogLock)
        {
            _installLogBuilder.AppendLine(message);
            queueUpdate = !_installLogUpdateQueued;
            if (queueUpdate) _installLogUpdateQueued = true;
        }
        if (queueUpdate)
        {
            Dispatcher.UIThread.Post(FlushInstallLogToUi, DispatcherPriority.Background);
        }
    }

    private void FlushInstallLogToUi()
    {
        string text;
        lock (_installLogLock)
        {
            text = _installLogBuilder.ToString();
            _installLogUpdateQueued = false;
        }
        InstallLogText = text;
    }

    private Task FlushPendingLogWrites() =>
        Dispatcher.UIThread.InvokeAsync(FlushInstallLogToUi, DispatcherPriority.Background).GetTask();

    private void ResetInstallLog()
    {
        lock (_installLogLock)
        {
            _installLogBuilder.Clear();
            _installLogUpdateQueued = false;
        }
        InstallLogText = string.Empty;
    }
    
    public void DisableInstallation()
    {
        InstallationDisabled = true;
    }

    public async Task UpdateLauncher()
    {
        try
        {
            await _updateService.CheckAndPerformUpdateAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Manual launcher update failed.", ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Update Failed!",
                $"Something went wrong while checking for launcher updates: {ex.Message}\nPlease try again later.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }
}
