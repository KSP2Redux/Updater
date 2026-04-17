using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;

namespace Ksp2Redux.Tools.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly INewsItemCollectionService _newsCollectionService;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IReleasesFeedService _releasesFeedService;
    private readonly ITabNavigatorService _tabNavigatorService;
    private readonly IFileSystem _fileSystem;
    private readonly ICacheService _cacheService;
    private readonly INewsService _newsService;
    private readonly IEnvironmentProvider _environmentProvider;
    private readonly IAssemblyService _assemblyService;

    [ObservableProperty] public partial InstallState CurrentInstallState { get; set; }

    public HomeTabViewModel HomeTab { get; }
    public CommunityTabViewModel CommunityTab { get; }
    public ModsTabViewModel ModsTab { get; }
    public SettingsTabViewModel SettingsTab { get; }
    
    public int CurrentTab { get; set; }

    public MainWindowViewModel(HomeTabViewModel homeTab, CommunityTabViewModel communityTab, ModsTabViewModel modsTab,
        SettingsTabViewModel settingsTabViewModel, IKsp2InstallService ksp2InstallService,
        INewsItemCollectionService newsCollectionService, ILauncherConfigService launcherConfigService,
        IReleasesFeedService releasesFeedService, ITabNavigatorService tabNavigatorService, IFileSystem fileSystem,
        ICacheService cacheService, INewsService newsService, IEnvironmentProvider environmentProvider, IAssemblyService assemblyService)
    {
        _newsCollectionService = newsCollectionService;
        _launcherConfigService = launcherConfigService;
        _releasesFeedService = releasesFeedService;
        _tabNavigatorService = tabNavigatorService;
        _fileSystem = fileSystem;
        _cacheService = cacheService;
        _newsService = newsService;
        _environmentProvider = environmentProvider;
        _assemblyService = assemblyService;

        _tabNavigatorService.CurrentTabChanged += CurrentTabChanged;
        
        LoadNews();
        ksp2InstallService.TryLoadKsp2Install();
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
        var releaseDownloadCacheDir = _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), "download-cache");
        _fileSystem.Directory.CreateDirectory(releaseDownloadCacheDir);
        foreach (var feed in _launcherConfigService.Config.Feeds)
        {
            Console.WriteLine($"Adding feed: {feed.Repository} / {feed.Filename}");
            var newFeed = new ManifestReleasesFeed(
                _fileSystem,
                _assemblyService,
                _launcherConfigService.GetLocalStorageDirectory(), feed.Repository,
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
        string tomlNewsContent = await _newsService.GetTomlContent();
        _newsService.LoadNewsFromToml(tomlNewsContent);
        List<News> newsList = await _newsService.FindAllNews();
        foreach (News news in newsList)
        {
            _newsCollectionService.Add(new Shared.NewsItemViewModel(_newsService, news));
        }
    }
    
    private void CurrentTabChanged(object? sender, ITabNavigatorService.CurrentTabChangedEventArgs e)
    {
        CurrentTab = e.CurrentTab;
        OnPropertyChanged(nameof(CurrentTab));
    }
}