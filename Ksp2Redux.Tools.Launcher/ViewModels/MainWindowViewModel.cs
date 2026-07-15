using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public const int HomeTabId = 0;
    public const int CommunityTabId = 1;
    public const int ModsTabId = 2;
    public const int SettingsTabId = 3;

    private int _lastNonSettingsTab = HomeTabId;

    private readonly INewsItemCollectionService _newsCollectionService;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IReleasesFeedService _releasesFeedService;
    private readonly ITabNavigatorService _tabNavigatorService;
    private readonly IFileSystem _fileSystem;
    private readonly INewsService _newsService;
    private readonly IManifestReleasesFeedProviderService _manifestReleasesFeedProviderService;
    private readonly IUpdateService _updateService;
    private readonly IKsp2DetectorService _ksp2DetectorService;
    private readonly IKsp2InstallService _ksp2InstallService;
    private readonly IMessageBoxService _messageBoxService;
    private readonly IWindowPlacementService _windowPlacementService;
    private readonly ILogService _log;

    [ObservableProperty]
    public partial InstallState CurrentInstallState { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateDownloading { get; set; }
    public HomeTabViewModel HomeTab { get; }
    public CommunityTabViewModel CommunityTab { get; }
    public ModsTabViewModel ModsTab { get; }
    public SettingsTabViewModel SettingsTab { get; }

    public Shared.NewsCollectionViewModel NewsCollectionViewModel { get; }

    [ObservableProperty]
    public partial int CurrentTab { get; set; }

    // Drives the blurred backdrop behind whichever glass panel (Home log, Community
    // article, Settings, Mods) is currently on screen. Home and Community don't always
    // have one showing, so this has to react to their own visibility state too, not just
    // which tab is selected.
    [ObservableProperty]
    public partial bool IsContentPanelVisible { get; set; }

    public MainWindowViewModel(HomeTabViewModel homeTab, CommunityTabViewModel communityTab, ModsTabViewModel modsTab,
        SettingsTabViewModel settingsTabViewModel, IKsp2InstallService ksp2InstallService,
        INewsItemCollectionService newsCollectionService, ILauncherConfigService launcherConfigService,
        IReleasesFeedService releasesFeedService, ITabNavigatorService tabNavigatorService, IFileSystem fileSystem,
        INewsService newsService, IManifestReleasesFeedProviderService manifestReleasesFeedProviderService, IUpdateService updateService,
        IKsp2DetectorService ksp2DetectorService, IMessageBoxService messageBoxService, IWindowPlacementService windowPlacementService, ILogService log)
    {
        _newsCollectionService = newsCollectionService;
        _launcherConfigService = launcherConfigService;
        _windowPlacementService = windowPlacementService;
        _releasesFeedService = releasesFeedService;
        _tabNavigatorService = tabNavigatorService;
        _fileSystem = fileSystem;
        _newsService = newsService;
        _manifestReleasesFeedProviderService = manifestReleasesFeedProviderService;
        _ksp2DetectorService = ksp2DetectorService;
        _messageBoxService = messageBoxService;
        _ksp2InstallService = ksp2InstallService;
        _log = log;

        _updateService = updateService;
        _updateService.DownloadingChanged += downloading =>
            Dispatcher.UIThread.Post(() => IsUpdateDownloading = downloading);

        _tabNavigatorService.CurrentTabChanged += CurrentTabChanged;

        _ksp2InstallService.ActiveInstallChanged += (_, _) =>
            Dispatcher.UIThread.Post(async () => await CheckActiveInstallWarnings());

        _ = LoadNews().ContinueWith(LogErrors);
        ksp2InstallService.TryLoadKsp2Install();
        ksp2InstallService.ApplyActiveInstallBootConfig();
        // ReleasesFeed =
        // [
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir),
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir)
        // ];
        // ReleasesFeed = [];

        HomeTab = homeTab;
        CommunityTab = communityTab;
        ModsTab = modsTab;
        SettingsTab = settingsTabViewModel;
        NewsCollectionViewModel = new Shared.NewsCollectionViewModel(newsCollectionService.NewsCollection);

        HomeTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeTabViewModel.IsInstallLogVisible))
            {
                UpdateContentPanelVisible();
            }
        };
        CommunityTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommunityTabViewModel.NewsVisible))
            {
                UpdateContentPanelVisible();
            }
        };
        UpdateContentPanelVisible();

        _ = InitializeAsync().ContinueWith(HandleInitializeFailure);

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(10)
        };

        timer.Tick += async (sender, args) =>
        {
            try
            {
                if (!await _updateService.CheckAndPerformUpdateAsync()) HomeTab.DisableInstallation();
            }
            catch (Exception ex)
            {
                // This is an async void-shaped event handler (DispatcherTimer.Tick), so an unhandled
                // exception here would crash the whole app on a background tick unrelated to anything
                // the user just did.
                _log.Error("Periodic update check failed unexpectedly.", ex);
            }
        };

        timer.Start();
    }

    private void LogErrors(Task antecedent)
    {
        if (antecedent.IsFaulted)
        {
            _log.Error("Background task faulted.", antecedent.Exception);
        }
    }

    /// <summary>
    /// Returns the placement the window should restore to, validated against the current
    /// monitor layout, or null to use the built-in defaults (first run, or garbage data).
    /// </summary>
    public WindowPlacement? GetRestoredWindowPlacement(IReadOnlyList<Avalonia.PixelRect> screenWorkingAreas, double minWidth, double minHeight) =>
        _windowPlacementService.Sanitize(_launcherConfigService.Config.WindowPlacement, screenWorkingAreas, minWidth, minHeight);

    public void SaveWindowPlacement(WindowPlacement placement)
    {
        _launcherConfigService.Config.WindowPlacement = placement;
        _launcherConfigService.Save();
    }

    public Task LaunchExternalLinkAsync(TopLevel? topLevel, string url)
        => ExternalLinkLauncher.LaunchAsync(topLevel, url, _messageBoxService, _log);

    // Startup initialization failing is more severe than a background task like the news feed
    // failing to load (which just leaves a list empty) - if this throws, feeds/install detection/
    // the update check may not have run at all, so tell the user instead of failing silently.
    private void HandleInitializeFailure(Task antecedent)
    {
        if (!antecedent.IsFaulted) return;
        _log.Error("Startup initialization failed.", antecedent.Exception);
        Dispatcher.UIThread.Post(async () =>
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Startup Error",
                "Something went wrong while starting up, so setup may be incomplete (feeds, install detection, or the update check may not have run). Check the log file for details, and consider restarting the launcher.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
        });
    }

    private async Task InitializeAsync()
    {
        _log.Info("MainWindow initializing.");
        if (Program.PartialUpdate)
        {
            _log.Warn("Partial update detected from a prior launch.");
            var updateDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "update");
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Partial Update Complete",
                $"After closing, please delete {updateDir}\nand confirm you still have the Updater locally.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
        }

        // First start the updater service
        if (!await _updateService.CheckAndPerformUpdateAsync()) HomeTab.DisableInstallation();

        // Now we want to check if any KSP2 installs are registered, and if not try and detect one
        if (_ksp2InstallService.Entries.Count == 0)
        {
            _log.Info("No KSP2 installs registered, attempting auto-detection.");
            if (_ksp2DetectorService.DetectKsp2InstallLocation() is { } installLocation)
            {
                _log.Info($"Detected KSP2 install at {installLocation}, prompting user to add.");
                var option = await _messageBoxService.ShowMessageBoxAsOwnedAsync("KSP2 Install Found",
                    $"Found KSP2 install at: {installLocation}\nWould you like to add it to Redux?\n(This can be changed in the settings.)", ButtonEnum.YesNo,
                    windowStartupLocation: WindowStartupLocation.CenterOwner);

                if (option == ButtonResult.Yes)
                {
                    _ksp2InstallService.AddInstall(installLocation);
                    _log.Info($"User added detected KSP2 install at {installLocation}.");
                }
                else
                {
                    _log.Info("User declined to add detected KSP2 install.");
                }
            }
            else
            {
                _log.Warn("KSP2 install was not auto-detected.");
                await _messageBoxService.ShowMessageBoxAsOwnedAsync("KSP2 Install Not Found!",
                    "Your KSP2 install was not detected, go to the settings tab to set it", ButtonEnum.Ok,
                    windowStartupLocation: WindowStartupLocation.CenterOwner);
            }
        }

        await CheckActiveInstallWarnings();

        // foreach (var feed in ReleasesFeed)
        // {
        //     await feed.Value.UpdateManifest();
        // }
        var releaseDownloadCacheDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "download-cache");
        _fileSystem.Directory.CreateDirectory(releaseDownloadCacheDir);
        _log.Info($"Loading {_launcherConfigService.Config.Feeds.Count} release feed(s) into {releaseDownloadCacheDir}.");
        foreach (var feed in _launcherConfigService.Config.Feeds)
        {
            _log.Info($"Adding feed: {feed.Repository} / {feed.Filename}");
            var newFeed = new ManifestReleasesFeed(
                _fileSystem, _manifestReleasesFeedProviderService, _log, releaseDownloadCacheDir, feed);
            _log.Info($"Updating manifest for feed: {feed.Repository} / {feed.Filename}");
            try
            {
                await newFeed.UpdateManifest();
                _log.Info($"Done adding feed: {feed.Repository} / {feed.Filename}, channel={newFeed.CurrentChannel}");
                _releasesFeedService.AddOrSet(newFeed.CurrentChannel, newFeed);
                SettingsTab.ValidChannels.Add(newFeed.CurrentChannel);
            }
            catch (Exception e)
            {
                _log.Error($"Failed to add feed {feed.Repository} / {feed.Filename}.", e);
            }
        }
        SettingsTab.SetLoaded();
        await HomeTab.UpdateVersionsList(false);
        _log.Info("MainWindow initialization complete.");

        // Now schedule update checks every 10 minutes
    }

    private async Task CheckActiveInstallWarnings()
    {
        var ksp2 = _ksp2InstallService.ClaimActiveInstallForFirstCheckThisSession();
        if (ksp2 is null) return;

        if (!ksp2.IsValid)
        {
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Invalid EXE",
                $"The configured KSP2 EXE path is not valid:\n{ksp2.ExePath}", ButtonEnum.Ok,
                windowStartupLocation: WindowStartupLocation.CenterOwner);
            return;
        }

        if (ksp2.VersionDetectionException is { } e)
        {
            _log.Error("Could not detect the installed KSP2 version.", e);
            await _messageBoxService.ShowMessageBoxAsOwnedAsync("Couldn't Detect Game Version",
                "KSP2 Redux couldn't figure out which version of the game is installed. " +
                "This can happen if the game files are missing, corrupted, or from an unsupported source.\n\n" +
                "You can still try launching or installing, but update checks may be unreliable. " +
                "Details were written to the log file (see Settings > Open Logs Folder) if you'd like to report this.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner);
        }
    }

    private async Task LoadNews()
    {
        await _newsService.FetchNews();
        List<News> newsList = await _newsService.FindAllNews();
        foreach (News news in newsList)
        {
            _newsCollectionService.Add(new Shared.NewsItemViewModel(news));
        }

        MaybeAutoSelectLatestCommunityNews();
    }

    private void CurrentTabChanged(object? sender, ITabNavigatorService.CurrentTabChangedEventArgs e)
    {
        CurrentTab = e.CurrentTab;
    }

    partial void OnCurrentTabChanging(int oldValue, int newValue)
    {
        if (oldValue != SettingsTabId)
        {
            _lastNonSettingsTab = oldValue;
        }
    }

    partial void OnCurrentTabChanged(int value)
    {
        if (value == CommunityTabId)
        {
            MaybeAutoSelectLatestCommunityNews();
        }

        UpdateContentPanelVisible();
    }

    private void UpdateContentPanelVisible()
    {
        IsContentPanelVisible = CurrentTab switch
        {
            HomeTabId => HomeTab.IsInstallLogVisible,
            CommunityTabId => CommunityTab.NewsVisible,
            ModsTabId => true,
            SettingsTabId => true,
            _ => false,
        };
    }

    // Opening Community with nothing selected yet is a dead end for the user, so jump
    // straight to the latest post instead of making them click into the list first.
    private void MaybeAutoSelectLatestCommunityNews()
    {
        if (CurrentTab != CommunityTabId || CommunityTab.NewsVisible) return;
        if (NewsCollectionViewModel.NewsCollection.Count == 0) return;

        CommunityTab.SetSelectedNewsId(NewsCollectionViewModel.NewsCollection[0].NewsId);
    }

    [RelayCommand]
    private void HandleEscape()
    {
        switch (CurrentTab)
        {
            case CommunityTabId when CommunityTab.NewsVisible:
                CommunityTab.DeselectNewsCommand.Execute(null);
                break;
            case HomeTabId when HomeTab.IsInstallLogVisible:
                HomeTab.IsInstallLogVisible = false;
                break;
            case SettingsTabId:
                CurrentTab = _lastNonSettingsTab;
                break;
        }
    }

    [RelayCommand]
    private void GoToTab(string tabId)
    {
        CurrentTab = int.Parse(tabId);
    }
}
