using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Mac;
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
    private readonly IWineRuntimeService _wineRuntimeService;
    private readonly IGameProcessService _gameProcessService;
    private readonly IInstallationStateService _installationStateService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IEnvironmentProvider _environmentProvider;
    private readonly IFileSystem _fileSystem;
    private readonly ILogService _log;
    private readonly ILocalizationService _localization;

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
    public partial string? MainButtonTooltip { get; set; }

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

    [ObservableProperty]
    public partial bool IsRepairAvailable { get; set; }

    private readonly StringBuilder _installLogBuilder = new();
    private readonly Lock _installLogLock = new();
    private bool _installLogUpdateQueued;
    private CancellationTokenSource? _cancelCurrentOperation;

    public bool IsOperationRunning => _cancelCurrentOperation is not null;
    private CancellationTokenSource? _gameSession;
    private bool _stoppingGame;

    private static readonly TimeSpan DirectStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SlowStartTimeout = TimeSpan.FromSeconds(120);

    public event EventHandler? GameLaunched;
    public event EventHandler? GameExited;
    public event EventHandler? FeedsRefreshed;

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => ReleaseChannels.DisplayName((item as GameVersionViewModel)?.Channel);

    /// <summary>
    /// Construct the home view using installation ownership and localized launcher messages.
    /// </summary>
    /// <param name="ksp2InstallService">The installation selection service.</param>
    /// <param name="launcherConfigService">The launcher configuration.</param>
    /// <param name="releasesFeedService">The release feeds.</param>
    /// <param name="installPlanService">The installation plan executor.</param>
    /// <param name="updateService">The launcher update service.</param>
    /// <param name="operatingSystemService">The host platform service.</param>
    /// <param name="messageBoxService">The confirmation and error dialogs.</param>
    /// <param name="environmentProvider">The process environment.</param>
    /// <param name="fileSystem">The filesystem abstraction.</param>
    /// <param name="log">The English file log.</param>
    /// <param name="wineRuntimeService">The existing Wine adapter.</param>
    /// <param name="gameProcessService">The game lifecycle service.</param>
    /// <param name="installationStateService">The installation ownership and launch leases.</param>
    /// <param name="localization">The localized message source, or the shared service when omitted.</param>
    public HomeTabViewModel(IKsp2InstallService ksp2InstallService,
        ILauncherConfigService launcherConfigService, IReleasesFeedService releasesFeedService, IInstallPlanService installPlanService, IUpdateService updateService, IOperatingSystemService operatingSystemService, IMessageBoxService messageBoxService, IEnvironmentProvider environmentProvider, IFileSystem fileSystem, ILogService log, IWineRuntimeService wineRuntimeService, IGameProcessService gameProcessService, IInstallationStateService installationStateService, ILocalizationService? localization = null)
    {
        _installationStateService = installationStateService;
        _localization = localization ?? LocalizationService.Default;
        MainButtonTooltip = _localization["Home.Tooltip.Loading"];
        _gameProcessService = gameProcessService;
        _ksp2InstallService = ksp2InstallService;
        _launcherConfigService = launcherConfigService;
        _releasesFeedService = releasesFeedService;
        _installPlanService = installPlanService;
        _updateService = updateService;
        _operatingSystemService = operatingSystemService;
        _wineRuntimeService = wineRuntimeService;
        _messageBoxService = messageBoxService;
        _environmentProvider = environmentProvider;
        _fileSystem = fileSystem;
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

        // The tooltip is computed text that is kept between updates, so it is worded again here.
        _localization.LanguageChanged += (_, _) => UpdateMainButtonState();
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
        foreach (var feed in _releasesFeedService.Feeds)
        {
            bool succeeded = await feed.UpdateManifest() || await OfferManifestMirrorSwitchAsync(feed);
            if (!succeeded) allSucceeded = false;
        }
        FeedRefreshFailed = !allSucceeded;
        FeedsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    internal async Task<bool> OfferManifestMirrorSwitchAsync(ManifestReleasesFeed feed)
    {
        if (FindPatchDownloadException(feed.LastUpdateException) is not { CanSwitchSource: true } downloadFailure ||
            _launcherConfigService.Config.PatchDownloadSource != downloadFailure.DownloadSource)
        {
            return false;
        }

        var alternateSource = downloadFailure.DownloadSource.GetAlternate();
        var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync(
            _localization.Format("Mirror.Manifest.Title", downloadFailure.DownloadSource),
            _localization.Format("Mirror.Manifest.Message", downloadFailure.DownloadSource, alternateSource),
            ButtonEnum.YesNo,
            windowStartupLocation: WindowStartupLocation.CenterOwner);
        if (result != ButtonResult.Yes)
        {
            return false;
        }

        _launcherConfigService.Config.PatchDownloadSource = alternateSource;
        _launcherConfigService.Save();
        return await feed.UpdateManifest();
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
    private Task RepairRedux() => RunPatchProcess(repair: true);

    /// <summary>
    /// Launch the selected game after cancellable owned-file verification.
    /// </summary>
    /// <returns>The startup and game-session tracking task.</returns>
    [RelayCommand]
    public async Task LaunchGame()
    {
        if (_cancelCurrentOperation is not null || _gameSession is not null) return;
        // Re-validate right before using it rather than trusting whatever was cached at the last
        // refresh - the install could have been moved, had its drive ejected, or been deleted in
        // the meantime if the app was left idle.
        _ksp2InstallService.TryLoadKsp2Install();
        if (_ksp2InstallService.Ksp2 is not { IsValid: true })
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.LaunchFailed.Title"],
                _localization["Home.LaunchFailed.NotDetected"], windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }
        var activeEntry = _ksp2InstallService.ActiveEntry;
        if (activeEntry is null) return;

        try
        {
            _installationStateService.EnsureLaunchAllowed(_ksp2InstallService.Ksp2.InstallDir!);
        }
        catch (Exception ex) when (ex is IOException or FilesInUseException)
        {
            _log.Warn(ex.Message);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Launch", ex.Message,
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancelCurrentOperation = cancellation;
        UpdateMainButtonState();
        try
        {
            // Before the Steam check: a config may still have LaunchThroughSteam set, and Steam on macOS cannot run KSP2.
            if (_operatingSystemService.IsMacOS())
            {
                await LaunchThroughWine(_ksp2InstallService.Ksp2, activeEntry.LaunchArguments, cancellation.Token);
                return;
            }

            if (activeEntry.LaunchThroughSteam)
            {
                var appId = activeEntry.SteamAppId;
                if (string.IsNullOrWhiteSpace(appId)) appId = "954850";
                var steamLaunch = new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{appId}",
                    UseShellExecute = true
                };
                if (!await TryStartGame(steamLaunch, "Failed to launch through Steam.",
                        ex => _localization.Format("Home.LaunchFailed.Steam", ex.UserMessage()), SlowStartTimeout, cancellation.Token)) return;
                return;
            }

            var directLaunch = new ProcessStartInfo
            {
                FileName = _ksp2InstallService.Ksp2.ExePath,
                WorkingDirectory = _ksp2InstallService.Ksp2.InstallDir
            };
            if (!string.IsNullOrWhiteSpace(activeEntry.LaunchArguments))
            {
                directLaunch.Arguments = activeEntry.LaunchArguments;
            }
            if (!await TryStartGame(directLaunch, "Failed to launch KSP2.",
                    ex => _localization.Format("Home.LaunchFailed.Direct", ex.UserMessage()), DirectStartTimeout, cancellation.Token)) return;
        }
        finally
        {
            _cancelCurrentOperation = null;
            UpdateMainButtonState();
        }
    }

    private async Task<bool> TryStartGame(ProcessStartInfo startInfo, string failureLog, Func<Exception, string> failureMessage,
        TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        try
        {
            string root = _ksp2InstallService.Ksp2!.InstallDir!;
            using IDisposable? lease = await Task.Run(
                () => _installationStateService.AcquireLaunchLease(root, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _gameProcessService.Start(startInfo);
            await TrackRunningGame(startTimeout, root, lease);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(failureLog, ex);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.LaunchFailed.Title"], failureMessage(ex),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return false;
        }
    }

    private async Task TrackRunningGame(TimeSpan startTimeout, string root, IDisposable? launchLease)
    {
        _gameSession = new CancellationTokenSource();
        var token = _gameSession.Token;
        var appeared = false;
        UpdateMainButtonState();
        try
        {
            bool started = launchLease is null
                ? await _gameProcessService.WaitForStartAsync(startTimeout, token)
                : await _gameProcessService.WaitForStartAsync(root, startTimeout, token);
            launchLease?.Dispose();
            if (started)
            {
                appeared = true;
                GameLaunched?.Invoke(this, EventArgs.Empty);
                await _gameProcessService.WaitForExitAsync(token);
            }
            else
            {
                _log.Warn($"KSP2 did not appear within {startTimeout.TotalSeconds:0} seconds of launching.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            launchLease?.Dispose();
            _gameSession.Dispose();
            _gameSession = null;
            _stoppingGame = false;
            UpdateMainButtonState();
            if (appeared) GameExited?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task StopGame()
    {
        if (_stoppingGame || _gameSession is null) return;

        var answer = await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.StopGame.Title"],
            _localization["Home.StopGame.Message"], ButtonEnum.YesNo, Icon.Warning,
            windowStartupLocation: WindowStartupLocation.CenterOwner);
        if (answer != ButtonResult.Yes || _gameSession is null) return;

        _stoppingGame = true;
        UpdateMainButtonState();
        try
        {
            await _gameProcessService.StopAsync();
            _gameSession?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to stop KSP2.", ex);
            _stoppingGame = false;
            UpdateMainButtonState();
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.StopFailed.Title"],
                _localization.Format("Home.StopFailed.Message", ex.UserMessage()), icon: Icon.Error,
                windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }

    private async Task LaunchThroughWine(Ksp2Install ksp2, string? launchArguments, CancellationToken cancellationToken)
    {
        if (_wineRuntimeService.Detect() is not { } runtime)
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.LaunchFailed.Title"],
                _localization["Home.LaunchFailed.NoRuntime"],
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        if (!_wineRuntimeService.IsRosettaInstalled())
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.Rosetta.Title"], WineRuntimeService.RosettaMissingMessage,
                icon: Icon.Warning, windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        try
        {
            await _wineRuntimeService.PrepareAsync(runtime, line => AppendToInstallLog(line.Localized), cancellationToken);

            var startInfo = _wineRuntimeService.CreateLaunchInfo(runtime, ksp2.ExePath, ksp2.InstallDir!, launchArguments);
            using IDisposable? lease = await Task.Run(
                () => _installationStateService.AcquireLaunchLease(ksp2.InstallDir!, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Info($"Launching KSP2 through {runtime.DisplayName} ({runtime.WineBinary}).");
            _gameProcessService.Start(startInfo);
            await TrackRunningGame(SlowStartTimeout, ksp2.InstallDir!, lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to launch KSP2 through {runtime.DisplayName}.", ex);
            UpdateMainButtonState();
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.LaunchFailed.Title"],
                _localization.Format("Home.LaunchFailed.Wine", runtime.DisplayName, ex.UserMessage()),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

    }

    [RelayCommand]
    public void CancelCurrentMainButtonAction()
    {
        if (_gameSession is not null)
        {
            _ = StopGame();
            return;
        }
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

        if (_gameSession is not null)
        {
            IsRepairAvailable = false;
            MainButtonShown = MainButtonState.Cancel;
            MainButtonEnabled = !_stoppingGame;
            MainButtonTooltip = _localization[_stoppingGame ? "Home.Tooltip.Stopping" : "Home.Tooltip.Stop"];
            return;
        }

        if (_cancelCurrentOperation is not null)
        {
            IsRepairAvailable = false;
            MainButtonShown = MainButtonState.Cancel;
            MainButtonEnabled = true;
            MainButtonTooltip = null;
            return;
        }

        _ksp2InstallService.TryLoadKsp2Install();
        var ksp2 = _ksp2InstallService.Ksp2;
        IsRepairAvailable = GetRepairPlan(ksp2, SelectedVersion) is not null;
        if (ksp2 is null || !ksp2.IsValid)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = _localization["Home.Tooltip.NotDetected"];
            return;
        }

        // IsValid only confirms the exe exists - it says nothing about whether we could actually
        // read its version. Falling through without this check let Install/Update stay enabled with
        // an unknown "from" version, which crashed or hung once clicked (see issue #26).
        if (ksp2.GameVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = _localization["Home.Tooltip.VersionUnknown"];
            return;
        }

        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Install;
            MainButtonTooltip = _localization["Home.Tooltip.SelectVersion"];
            return;
        }

        var linuxLaunchBlocked = (_operatingSystemService.IsLinux() && !(_ksp2InstallService.ActiveEntry?.LaunchThroughSteam ?? false))
                                 || (_operatingSystemService.IsMacOS() && _wineRuntimeService.Detect() is null);
        var linuxLaunchBlockedTooltip = _operatingSystemService.IsMacOS()
            ? _localization["Home.Tooltip.NoRuntime"]
            : _localization["Home.Tooltip.LinuxSteam"];

        var installationDisabledTooltip = _localization["Home.Tooltip.LauncherUpdate"];

        if (ksp2.Distribution != Distribution.Redux)
        {
            MainButtonEnabled = true;
            MainButtonTooltip = null;
            if (selectedVersion.Channel == "installed" || IsInstalledSelection(ksp2, selectedVersion))
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

        if (selectedVersion.Channel == "installed" || IsInstalledSelection(ksp2, selectedVersion))
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

    private bool IsInstalledSelection(Ksp2Install install, GameVersionViewModel selection)
    {
        InstallPlan? plan = _releasesFeedService.ReleasesFeed.TryGetValue(selection.Channel, out var feed)
            ? feed.GetFullSourcePlan(selection.Version) : null;
        return install.IsInstalledVersion(selection.Version, plan);
    }

    private InstallPlan? GetRepairPlan(Ksp2Install? install, GameVersionViewModel? selection)
    {
        if (InstallationDisabled || !_operatingSystemService.IsWindows() || install?.ManagedRelease is not { } installed || selection is null)
            return null;

        bool repairInstalled = selection.Channel == "installed";
        string channel = repairInstalled ? installed.Channel : selection.Channel;
        if (!_releasesFeedService.ReleasesFeed.TryGetValue(channel, out var feed))
            return null;

        var plan = feed.GetFullSourcePlan(selection.Version, repair: true);
        return repairInstalled && (plan is null || !install.IsInstalledVersion(selection.Version, plan)) ? null : plan;
    }

    private void RebuildVersionsCollection()
    {
        // We want to do this for the moment, we could fix this logic later at some point
        _ksp2InstallService.TryLoadKsp2Install();
        
        Versions.Clear();
        
        GameVersionViewModel? currentVersion = null;
        if (_ksp2InstallService.Ksp2?.GameVersion is { } installedVersion)
        {
            // Promoted builds can still embed the beta channel. Published metadata
            // supplies the current channel and label, with stable taking precedence.
            var publishedVersion = _ksp2InstallService.Ksp2.ManagedRelease is not null ? null : _releasesFeedService.ReleasesFeed.Values
                .SelectMany(feed => feed.GetAllVersions())
                .Where(version => version.Equals(installedVersion))
                .OrderByDescending(version => version.Channel == ReleaseChannels.STABLE)
                .FirstOrDefault();
            currentVersion = new GameVersionViewModel(publishedVersion ?? installedVersion)
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
            if (latest is not null && (_ksp2InstallService.Ksp2 is not { } active ||
                !IsInstalledSelection(active, new GameVersionViewModel(latest))))
            {
                defaultVersion = Versions.FirstOrDefault(v =>
                    v.Channel == activeChannel && v.Version.Equals(latest)) ?? currentVersion;
            }
        }
        SelectedVersion = defaultVersion;
    }

    private async Task RunPatchProcess(bool repair = false)
    {
        if (_cancelCurrentOperation is not null || _gameSession is not null || InstallationDisabled)
            return;

        _ksp2InstallService.TryLoadKsp2Install();
        var ksp2 = _ksp2InstallService.Ksp2;
        var selectedVersion = SelectedVersion;
        if (ksp2 is null || await BlockIfLauncherInsideInstallDir(ksp2) || selectedVersion is null)
            return;
        var repairPlan = repair ? GetRepairPlan(ksp2, selectedVersion) : null;
        if (repair && repairPlan is null)
            return;

        if (!repair && ksp2.GameVersion is null)
        {
            Log(_localization.Message("Home.Log.VersionUnknown"));
            return;
        }

        ResetInstallLog();
        IsInstallLogVisible = true;
        IsProgressVisible = true;
        DownloadProgressMb = 0;
        DownloadProgressTotalMb = 0;
        InstallProgressSteps = 0;
        InstallProgressTotalSteps = 1;
        // Set up process cancellation trigger.
        _cancelCurrentOperation = new CancellationTokenSource();
        UpdateMainButtonState();

        Log(_localization.Message("Home.Log.CreatingPlan"));

        try
        {
            while (true)
            {
                var plan = repairPlan ?? _releasesFeedService.ReleasesFeed[selectedVersion.Channel]
                    .GetPatchListToVersion(ksp2.GameVersion!, selectedVersion.Version);
                if (plan.Steps.Count == 0 && !plan.IsFullSource)
                {
                    LogError(_localization.Message("Home.Log.NoRoute", selectedVersion.Version));
                    Log(_localization.Message("Home.Log.NothingChanged"));
                    return;
                }
                try
                {
                    await RunPlanOnInstall(plan, ksp2);
                    break;
                }
                catch (Exception e) when (CanOfferMirrorSwitch(e))
                {
                    var failedSource = _launcherConfigService.Config.PatchDownloadSource;
                    var alternateSource = failedSource.GetAlternate();
                    var result = await _messageBoxService.ShowMessageBoxAsOwnedAsync(
                        _localization.Format("Mirror.Download.Title", failedSource),
                        _localization.Format("Mirror.Download.Message", failedSource, alternateSource),
                        ButtonEnum.YesNo,
                        windowStartupLocation: WindowStartupLocation.CenterOwner);
                    if (result != ButtonResult.Yes)
                    {
                        throw;
                    }

                    _cancelCurrentOperation.Cancel();
                    _cancelCurrentOperation.Dispose();
                    _cancelCurrentOperation = new CancellationTokenSource();
                    _launcherConfigService.Config.PatchDownloadSource = alternateSource;
                    _launcherConfigService.Save();
                    Log(_localization.Message("Home.Log.Retrying", alternateSource));
                }
            }
            RecordInstalledVersion();
            Log(_localization.Message("Home.Log.Installed"));
        }
        catch (OperationCanceledException)
        {
            Log(_localization.Message("Home.Log.Cancelled"));
        }
        catch (InsufficientDiskSpaceException e)
        {
            await ShowNotEnoughDiskSpace(e);
        }
        catch (FilesInUseException e)
        {
            await ShowFilesInUse(e);
        }
        catch (InstallFailedException e)
        {
            LogError(e.ToLogMessage(), e);
        }
        catch (Exception e)
        {
            LogError(_localization.Message("Home.Log.UpdateError", e.ToLogMessage()), e);
            Log(_localization.Message("Home.Log.InvalidState"));
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

    private void RecordInstalledVersion()
    {
        _ksp2InstallService.TryLoadKsp2Install();
        _ksp2InstallService.UpdateLastInstalledVersion(_ksp2InstallService.Ksp2?.GameVersion);
    }

    private static PatchDownloadException? FindPatchDownloadException(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PatchDownloadException downloadException)
            {
                return downloadException;
            }
        }
        return null;
    }

    private bool CanOfferMirrorSwitch(Exception exception) =>
        FindPatchDownloadException(exception) is { CanSwitchSource: true } downloadFailure &&
        downloadFailure.DownloadSource == _launcherConfigService.Config.PatchDownloadSource;

    public async Task InstallFromPatchFile(string path)
    {
        if (_cancelCurrentOperation is not null || _gameSession is not null)
            return;

        _ksp2InstallService.TryLoadKsp2Install();
        if (_ksp2InstallService.Ksp2 is { } target && await BlockIfLauncherInsideInstallDir(target))
        {
            return;
        }

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
        if (ksp2 is null)
        {
            IsProgressVisible = false;
            return;
        }
        
        _cancelCurrentOperation = new CancellationTokenSource();
        UpdateMainButtonState();
        
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            RecordInstalledVersion();
            Log(_localization.Message("Home.Log.Installed"));
        }
        catch (OperationCanceledException)
        {
            Log(_localization.Message("Home.Log.Cancelled"));
        }
        catch (InsufficientDiskSpaceException e)
        {
            await ShowNotEnoughDiskSpace(e);
        }
        catch (FilesInUseException e)
        {
            await ShowFilesInUse(e);
        }
        catch (InstallFailedException e)
        {
            LogError(e.ToLogMessage(), e);
        }
        catch (Exception e)
        {
            LogError(_localization.Message("Home.Log.UpdateError", e.ToLogMessage()), e);
            Log(_localization.Message("Home.Log.InvalidState"));
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

    // Runs the same uninstall plan as the command line. The plan undoes the bundle conversion before it restores
    // the stock files, because the restore deletes the conversion journal and the converted bundles in
    // StreamingAssets would stay converted, which draws the main menu magenta.
    /// <summary>
    /// Restore stock through the installation plan and refresh the selected installation.
    /// </summary>
    /// <returns>A task that completes after restoration and launcher state refresh.</returns>
    public async Task UninstallRedux()
    {
        if (_cancelCurrentOperation is not null || _gameSession is not null)
            return;

        _ksp2InstallService.TryLoadKsp2Install();
        if (_ksp2InstallService.Ksp2 is not { } ksp2)
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization["Settings.UninstallRedux.NoInstall"], windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        if (await BlockIfLauncherInsideInstallDir(ksp2))
        {
            return;
        }

        ResetInstallLog();
        IsInstallLogVisible = true;
        IsProgressVisible = true;
        DownloadProgressMb = 0;
        DownloadProgressTotalMb = 0;
        InstallProgressSteps = 0;
        InstallProgressTotalSteps = 1;

        _cancelCurrentOperation = new CancellationTokenSource();
        UpdateMainButtonState();

        var plan = new InstallPlan();
        plan.Uninstall();

        var uninstalled = false;
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log(_localization.Message("Settings.UninstallRedux.Done"));
            uninstalled = true;
        }
        catch (OperationCanceledException)
        {
            Log(_localization.Message("Home.Log.Cancelled"));
        }
        catch (FilesInUseException e)
        {
            await ShowFilesInUse(e);
        }
        catch (Exception e)
        {
            LogError(e.ToLogMessage(), e);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Error.Title"],
                _localization.Format("Settings.UninstallRedux.Failed", e.UserMessage()),
                windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
        finally
        {
            _cancelCurrentOperation = null;
            await FlushPendingLogWrites();
            IsProgressVisible = false;
            _ksp2InstallService.TryLoadKsp2Install();
            await UpdateVersionsList();
        }

        if (uninstalled)
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Dialog.Done.Title"],
                _localization["Settings.UninstallRedux.Done"], windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }

    // Certain install plans replace or delete the launcher's own files. If the launcher is running
    // from inside the KSP2 install folder those files are locked (Windows) or would be pulled out
    // from under the running process, so the install fails partway. Warn and refuse rather than let
    // it break. This is a hard block - there's no safe "proceed anyway".
    private async Task<bool> BlockIfLauncherInsideInstallDir(Ksp2Install ksp2)
    {
        if (!IsLauncherInsideInstallDir(ksp2.InstallDir)) return false;

        _log.Warn($"Refusing to install: launcher is running from inside the install directory \"{ksp2.InstallDir}\".");
        await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.CantInstallHere.Title"],
            _localization["Home.CantInstallHere.Message"],
            ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
        return true;
    }

    private async Task ShowNotEnoughDiskSpace(InsufficientDiskSpaceException e)
    {
        LogError(e.ToLogMessage(), e);
        Log(_localization.Message("Home.Log.NothingChanged"));
        await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.DiskSpace.Title"],
            _localization.Format("Home.DiskSpace.Message", e.UserMessage(), e.Path),
            ButtonEnum.Ok, Icon.Warning, windowStartupLocation: WindowStartupLocation.CenterOwner);
    }

    private async Task ShowFilesInUse(FilesInUseException e)
    {
        LogError(e.ToLogMessage(), e);
        await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.FilesInUse.Title"], e.UserMessage(),
            ButtonEnum.Ok, Icon.Warning, windowStartupLocation: WindowStartupLocation.CenterOwner);
    }

    private bool IsLauncherInsideInstallDir(string installDir)
    {
        var launcherPath = _environmentProvider.ProcessPath;
        if (string.IsNullOrEmpty(launcherPath) || string.IsNullOrEmpty(installDir)) return false;

        var launcherDir = _fileSystem.Path.GetDirectoryName(_fileSystem.Path.GetFullPath(launcherPath));
        if (string.IsNullOrEmpty(launcherDir)) return false;

        var comparison = _operatingSystemService.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var installFull = TrimTrailingSeparators(_fileSystem.Path.GetFullPath(installDir));
        var launcherFull = TrimTrailingSeparators(launcherDir);

        return launcherFull.Equals(installFull, comparison)
            || launcherFull.StartsWith(installFull + _fileSystem.Path.DirectorySeparatorChar, comparison);
    }

    private string TrimTrailingSeparators(string path) =>
        path.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar);

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

    // The log file always gets English, and the install log on screen the user's language.
    internal void Log(LogMessage message)
    {
        _log.Info(message.English);
        AppendToInstallLog(message.Localized);
    }

    // Real install/patch failures used to go through Log(), which always writes at Info - grepping a
    // user's log file for "ERROR" to find out what went wrong turned up nothing, since the failure and
    // its stack trace were filed alongside routine progress lines.
    internal void LogError(LogMessage message, Exception? exception = null)
    {
        _log.Error(message.English, exception);
        AppendToInstallLog(message.Localized);
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
        UpdateMainButtonState();
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
            await _messageBoxService.ShowMessageBoxAsOwnedAsync(_localization["Home.UpdateFailed.Title"],
                _localization.Format("Home.UpdateFailed.Message", ex.UserMessage()), windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }
}
