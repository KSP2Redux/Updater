using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public const int SettingsTabId = 3;
    
    private readonly INewsItemCollectionService _newsCollectionService;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IReleasesFeedService _releasesFeedService;
    private readonly ITabNavigatorService _tabNavigatorService;
    private readonly IFileSystem _fileSystem;
    private readonly INewsService _newsService;
    private readonly IManifestReleasesFeedProviderService _manifestReleasesFeedProviderService;
    private readonly IUpdateService _updateService;
    private readonly IKsp2DetectorService _ksp2DetectorService;

    [ObservableProperty] public partial InstallState CurrentInstallState { get; set; }

    [ObservableProperty] public partial bool IsUpdateDownloading { get; set; }

    public HomeTabViewModel HomeTab { get; }
    public CommunityTabViewModel CommunityTab { get; }
    public ModsTabViewModel ModsTab { get; }
    public SettingsTabViewModel SettingsTab { get; }

    [ObservableProperty] public partial int CurrentTab { get; set; }

    public MainWindowViewModel(HomeTabViewModel homeTab, CommunityTabViewModel communityTab, ModsTabViewModel modsTab,
        SettingsTabViewModel settingsTabViewModel, IKsp2InstallService ksp2InstallService,
        INewsItemCollectionService newsCollectionService, ILauncherConfigService launcherConfigService,
        IReleasesFeedService releasesFeedService, ITabNavigatorService tabNavigatorService, IFileSystem fileSystem,
        INewsService newsService, IManifestReleasesFeedProviderService manifestReleasesFeedProviderService, IUpdateService updateService,
        IKsp2DetectorService ksp2DetectorService)
    {
        _newsCollectionService = newsCollectionService;
        _launcherConfigService = launcherConfigService;
        _releasesFeedService = releasesFeedService;
        _tabNavigatorService = tabNavigatorService;
        _fileSystem = fileSystem;
        _newsService = newsService;
        _manifestReleasesFeedProviderService = manifestReleasesFeedProviderService;
        _ksp2DetectorService = ksp2DetectorService;
        
        _updateService = updateService;
        _updateService.DownloadingChanged += downloading =>
            Dispatcher.UIThread.Post(() => IsUpdateDownloading = downloading);

        _tabNavigatorService.CurrentTabChanged += CurrentTabChanged;
        
        _ = LoadNews().ContinueWith(LogErrors);
        ksp2InstallService.TryLoadKsp2Install();
        // ReleasesFeed =
        // [
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir),
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir)
        // ];
        // ReleasesFeed = [];

        _ = InitializeAsync().ContinueWith(LogErrors);

        HomeTab = homeTab;
        CommunityTab = communityTab;
        ModsTab = modsTab;
        SettingsTab = settingsTabViewModel;

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(10)
        };

        timer.Tick += async (sender, args) =>
        {
            if (!await _updateService.CheckAndPerformUpdateAsync()) HomeTab.DisableInstallation();
        };
        
        timer.Start();
    }

    private void LogErrors(Task antecedent)
    {
        if (antecedent.IsFaulted)
        {
            Console.WriteLine(antecedent.Exception);
        }
    }

    private async Task InitializeAsync()
    {
        if (Program.PartialUpdate)
        {
            var updateDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "update");
            await MessageBoxManager.GetMessageBoxStandard("Partial Update Complete",
                $"After closing, please delete {updateDir}\nand confirm you still have the Updater locally.",
                ButtonEnum.Ok, windowStartupLocation: WindowStartupLocation.CenterOwner).ShowAsOwnedAsync();
        }

        // First start the updater service
        if (!await _updateService.CheckAndPerformUpdateAsync()) HomeTab.DisableInstallation();
        
        // Now we want to check if KSP2 is set, and if not try and set it
        if (string.IsNullOrEmpty(_launcherConfigService.Config.Ksp2InstallPath))
        {
            if (_ksp2DetectorService.DetectKsp2InstallLocation() is { } installLocation)
            {
                var option = await MessageBoxManager.GetMessageBoxStandard("KSP2 Install Found",
                    $"Found KSP2 install at: {installLocation}\nWould you like to set this as the install used for Redux?\n(This can be changed in the settings.)", ButtonEnum.YesNo,
                    windowStartupLocation: WindowStartupLocation.CenterOwner).ShowAsOwnedAsync();

                if (option == ButtonResult.Yes)
                {
                    _launcherConfigService.Config.Ksp2InstallPath = installLocation;
                    _launcherConfigService.Save();
                    SettingsTab.UpdateInstallPath();
                }
            }
            else
            {
                await MessageBoxManager.GetMessageBoxStandard("KSP2 Install Not Found!",
                    "Your KSP2 install was not detected, go to the settings tab to set it", ButtonEnum.Ok,
                    windowStartupLocation: WindowStartupLocation.CenterOwner).ShowAsOwnedAsync();
            }
        }
        
        // foreach (var feed in ReleasesFeed)
        // {
        //     await feed.Value.UpdateManifest();
        // }
        var releaseDownloadCacheDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "download-cache");
        _fileSystem.Directory.CreateDirectory(releaseDownloadCacheDir);
        foreach (var feed in _launcherConfigService.Config.Feeds)
        {
            Console.WriteLine($"Adding feed: {feed.Repository} / {feed.Filename}");
            var newFeed = new ManifestReleasesFeed(
                _fileSystem, _manifestReleasesFeedProviderService, releaseDownloadCacheDir, feed);
            Console.WriteLine("Updating feed manifest");
            try
            {
                await newFeed.UpdateManifest();
                Console.WriteLine($"Done adding feed: {feed.Repository} / {feed.Filename}");
                _releasesFeedService.AddOrSet(newFeed.CurrentChannel, newFeed);
                SettingsTab.ValidChannels.Add(newFeed.CurrentChannel);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
        SettingsTab.SetLoaded();
        await HomeTab.UpdateVersionsList(false);
        
        // Now schedule update checks every 10 minutes
    }

    private async Task LoadNews()
    {
        await _newsService.FetchNews();
        List<News> newsList = await _newsService.FindAllNews();
        foreach (News news in newsList)
        {
            _newsCollectionService.Add(new Shared.NewsItemViewModel(_newsService, news));
        }
    }
    
    private void CurrentTabChanged(object? sender, ITabNavigatorService.CurrentTabChangedEventArgs e)
    {
        CurrentTab = e.CurrentTab;
    }
}