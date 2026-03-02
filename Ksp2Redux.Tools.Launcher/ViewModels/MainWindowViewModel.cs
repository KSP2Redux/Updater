using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;

namespace Ksp2Redux.Tools.Launcher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<Shared.NewsItemViewModel> NewsCollection { get; set; } = [];

    [ObservableProperty] public partial InstallState CurrentInstallState { get; set; }

    public HomeTabViewModel HomeTab { get; }
    public CommunityTabViewModel CommunityTab { get; }
    public ModsTabViewModel ModsTab { get; }
    public SettingsTabViewModel SettingsTab { get; }

    public LauncherConfig Config { get; }
    public Ksp2Install? Ksp2 { get; private set; }
    public Dictionary<string, ManifestReleasesFeed> ReleasesFeed { get; private set; }
    public int CurrentTab { get; set; }

    public MainWindowViewModel()
    {
        Config = LauncherConfig.GetOrCreateCurrentConfig();

        LoadNews();
        TryLoadKsp2Install();
        // ReleasesFeed =
        // [
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir),
        //     new ManifestReleasesFeed(LauncherConfig.GetLocalStorageDirectory(), Config.ReduxRepoUrl, Config.Pat, releaseDownloadCacheDir)
        // ];
        ReleasesFeed = [];

        _ = InitializeAsync();

        HomeTab = new HomeTabViewModel(this);
        CommunityTab = new CommunityTabViewModel(NewsCollection);
        ModsTab = new ModsTabViewModel();
        SettingsTab = new SettingsTabViewModel(Config, this);
    }

    private async Task InitializeAsync()
    {
        // foreach (var feed in ReleasesFeed)
        // {
        //     await feed.Value.UpdateManifest();
        // }
        var releaseDownloadCacheDir = Path.Combine(LauncherConfig.GetLocalStorageDirectory(), "download-cache");
        Directory.CreateDirectory(releaseDownloadCacheDir);
        foreach (var feed in Config.Feeds)
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
                ReleasesFeed[newFeed.CurrentChannel] = newFeed;
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
            NewsCollection.Add(new Shared.NewsItemViewModel(news));
        }
    }

    public void TryLoadKsp2Install()
    {
        if (!string.IsNullOrWhiteSpace(Config.Ksp2InstallPath))
        {
            Ksp2 = new(Config.Ksp2InstallPath);
        }
    }

    public void GoToHome()
    {
        CurrentTab = 0;
        OnPropertyChanged(nameof(CurrentTab));
    }
}