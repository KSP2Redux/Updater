using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;
using MsBox.Avalonia;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public partial class HomeTabViewModel : ViewModelBase
{
    private readonly IKsp2InstallService _ksp2InstallService;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IReleasesFeedService _releasesFeedService;
    private readonly IInstallPlanService _installPlanService;
    private readonly IUpdateService _updateService;
    
    public NewsCollectionViewModel NewsCollectionViewModel { get; set; }

    public ObservableCollection<GameVersionViewModel> Versions { get; } = [];
    public ObservableCollection<Ksp2InstallChoiceViewModel> Installs { get; } = [];

    [ObservableProperty] public partial GameVersionViewModel? SelectedVersion { get; set; }
    [ObservableProperty] public partial Ksp2InstallChoiceViewModel? SelectedInstall { get; set; }
    [ObservableProperty] public partial bool IsInstallSwitcherEnabled { get; private set; }

    public enum MainButtonState
    {
        Launch,
        Install,
        Update,
        Cancel,
    }
    [ObservableProperty] public partial MainButtonState MainButtonShown { get; private set; }
    [ObservableProperty] public partial bool MainButtonEnabled { get; private set; }
    [ObservableProperty] public partial string MainButtonTooltip { get; private set; } = "Loading...";
    [ObservableProperty] public partial bool IsProgressVisible { get; private set; } = false;
    [ObservableProperty] public partial float DownloadProgressMb { get; private set; } = 250;
    [ObservableProperty] public partial float DownloadProgressTotalMb { get; private set; } = 450;
    [ObservableProperty] public partial float InstallProgressSteps { get; private set; } = 1;
    [ObservableProperty] public partial float InstallProgressTotalSteps { get; private set; } = 3;
    [ObservableProperty] public partial bool IsInstallLogVisible { get; private set; } = false;
    [ObservableProperty] public partial string InstallLogText { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool InstallationDisabled { get; private set; } = false;
    private readonly StringBuilder _installLogBuilder = new();
    private readonly object _installLogLock = new();
    private bool _installLogUpdateQueued;

    private CancellationTokenSource? cancelCurrentOperation;

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => (item as GameVersionViewModel)?.Channel ?? string.Empty;

    public HomeTabViewModel(IKsp2InstallService ksp2InstallService, INewsItemCollectionService newsCollectionService,
        ILauncherConfigService launcherConfigService, IReleasesFeedService releasesFeedService, IInstallPlanService installPlanService, IUpdateService updateService)
    {
        _ksp2InstallService = ksp2InstallService;
        _launcherConfigService = launcherConfigService;
        _releasesFeedService = releasesFeedService;
        _installPlanService = installPlanService;
        _updateService = updateService;

        NewsCollectionViewModel = new NewsCollectionViewModel(newsCollectionService.NewsCollection);
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
        RebuildVersionsCollection();
        UpdateMainButtonState();
    }
    private async Task UpdateAsync()
    {
        foreach (var feed in _releasesFeedService.ReleasesFeed)
        {
            await feed.Value.UpdateManifest();
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
        if (_ksp2InstallService.Ksp2 is null) return;
        var activeEntry = _ksp2InstallService.ActiveEntry;
        if (activeEntry is null) return;

        if (activeEntry.LaunchThroughSteam)
        {
            var appId = activeEntry.SteamAppId;
            if (string.IsNullOrWhiteSpace(appId)) appId = "954850";
            var startInfo = new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{appId}",
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            return;
        }

        MainButtonEnabled = false;
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
        MainButtonEnabled = true;
    }

    [RelayCommand]
    public void CancelCurrentMainButtonAction()
    {
        cancelCurrentOperation?.Cancel();
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

        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Install;
            MainButtonTooltip = "Please select a version to install or launch.";
            return;
        }

        var linuxLaunchBlocked = OperatingSystem.IsLinux() && !(_ksp2InstallService.ActiveEntry?.LaunchThroughSteam ?? false);
        const string linuxLaunchBlockedTooltip = "Enable \"Launch through Steam\" in settings to launch on Linux.";

        if (ksp2.Distribution != Distribution.Redux)
        {
            MainButtonEnabled = true;
            if (selectedVersion.Version.Equals(ksp2.GameVersion) || selectedVersion.Channel == "installed")
            {
                MainButtonShown = MainButtonState.Launch;
                MainButtonTooltip = "Launch Stock KSP2";
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
                MainButtonTooltip = "Install Ksp2Redux";
            }
            return;
        }

        if (selectedVersion.Version.Equals(ksp2.GameVersion) || selectedVersion.Channel == "installed")
        {
            MainButtonEnabled = true;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = "Launch Ksp2Redux";
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
            MainButtonTooltip = "Update Ksp2Redux";
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
        
        // Set up process cancellation trigger.
        MainButtonShown = MainButtonState.Cancel;
        MainButtonEnabled = true;
        MainButtonTooltip = "Cancel installation";
        cancelCurrentOperation = new CancellationTokenSource();

        Log("Creating install plan");
            
        var plan = _releasesFeedService.ReleasesFeed[SelectedVersion.Channel]
            .GetPatchListToVersion(_ksp2InstallService.Ksp2!.GameVersion!, SelectedVersion.Version);
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log("KSP2 Redux Successfully Installed");
        }
        catch (Exception e)
        {
            Log($"Error updating Redux: {e.Message}");
            Log($"Stack Trace: {e.StackTrace}");
            Log($"Redux may be in an invalid state, try uninstalling and reinstalling");
        }
        finally
        {
            cancelCurrentOperation = null;
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
        MainButtonTooltip = "Cancel installation";
        cancelCurrentOperation = new CancellationTokenSource();
        
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log("KSP2 Redux Successfully Installed");
        }
        catch (Exception e)
        {
            Log($"Error updating Redux: {e.Message}");
            Log($"Stack Trace: {e.StackTrace}");
            Log($"Redux may be in an invalid state, try uninstalling and reinstalling");
        }
        finally
        {
            cancelCurrentOperation = null;
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

        await Task.Run(
            () => _installPlanService.ApplyToFolder(
                plan,
                ksp2.InstallDir,
                Log,
                UpdateDownloadProgress,
                UpdateStepsProgress,
                cancelCurrentOperation!.Token),
            cancelCurrentOperation!.Token);

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
        Console.WriteLine(message);
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
        await _updateService.CheckAndPerformUpdateAsync();
    }
}
