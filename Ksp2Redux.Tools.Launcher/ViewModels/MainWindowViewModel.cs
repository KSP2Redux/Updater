using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;

namespace Ksp2Redux.Tools.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private IKsp2InstallService _ksp2InstallService;
    private INewsItemCollectionService _newsCollectionService;
    private ILauncherConfigService _launcherConfigService;
    private IReleasesFeedService _releasesFeedService;
    private ITabNavigatorService _tabNavigatorService;
    
    [ObservableProperty] public partial InstallState CurrentInstallState { get; set; }

    public HomeTabViewModel HomeTab { get; }
    public CommunityTabViewModel CommunityTab { get; }
    public ModsTabViewModel ModsTab { get; }
    public SettingsTabViewModel SettingsTab { get; }
    
    public int CurrentTab { get; set; }

    public MainWindowViewModel(HomeTabViewModel homeTab, CommunityTabViewModel communityTab, ModsTabViewModel modsTab,
        SettingsTabViewModel settingsTabViewModel, IKsp2InstallService ksp2InstallService,
        INewsItemCollectionService newsCollectionService, ILauncherConfigService launcherConfigService,
        IReleasesFeedService releasesFeedService, ITabNavigatorService tabNavigatorService)
    {
        _ksp2InstallService = ksp2InstallService;
        _newsCollectionService = newsCollectionService;
        _launcherConfigService = launcherConfigService;
        _releasesFeedService = releasesFeedService;
        _tabNavigatorService = tabNavigatorService;
        
        _tabNavigatorService.CurrentTabChanged += CurrentTabChanged;
        
        LoadNews();
        _ksp2InstallService.TryLoadKsp2Install();
        // ReleasesFeed =
        // [
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir),
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir)
        // ];
        // ReleasesFeed = [];

        _ = InitializeAsync();

        HomeTab = homeTab;
        CommunityTab = communityTab;
        ModsTab = modsTab;
        SettingsTab = settingsTabViewModel;
    }

    private async Task InitializeAsync()
    {
        // foreach (var feed in ReleasesFeed)
        // {
        //     await feed.Value.UpdateManifest();
        // }
        var releaseDownloadCacheDir = Path.Combine(LauncherConfig.GetLocalStorageDirectory(), "download-cache");
        Directory.CreateDirectory(releaseDownloadCacheDir);
        foreach (var feed in _launcherConfigService.Config.Feeds)
        {
            Console.WriteLine($"Adding feed: {feed.Repository} / {feed.Filename}");
            var newFeed = new ManifestReleasesFeed(
                LauncherConfig.GetLocalStorageDirectory(), feed.Repository,
                releaseDownloadCacheDir, feed.Filename, feed.Token);
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
    }

    private async void LoadNews()
    {
        string tomlNewsContent = await News.GetTomlContent();
        News.LoadNewsFromToml(tomlNewsContent);
        List<News> newsList = await News.FindAllNews();
        foreach (News news in newsList)
        {
            _newsCollectionService.Add(new Shared.NewsItemViewModel(news));
        }
    }
    
    private void CurrentTabChanged(object? sender, ITabNavigatorService.CurrentTabChangedEventArgs e)
    {
        CurrentTab = e.CurrentTab;
        OnPropertyChanged(nameof(CurrentTab));
    }
}