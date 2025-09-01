using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public partial class HomeTabViewModel : ViewModelBase
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; }

    public ObservableCollection<GameVersionViewModel> Versions { get; } = new();

    [ObservableProperty] public partial GameVersionViewModel? SelectedVersion { get; set; }

    private readonly GitHubReleasesFeed releasesFeed;

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => (item as GameVersionViewModel)?.Channel ?? string.Empty;

    public HomeTabViewModel(ObservableCollection<NewsItemViewModel> newsCollection, GitHubReleasesFeed releasesFeed)
    {
        NewsCollection = newsCollection;

        this.releasesFeed = releasesFeed;
        RebuildVersionsCollection();
    }


    [RelayCommand]
    public async Task UpdateVersionsList()
    {
        await releasesFeed.UpdateFromApi();
        RebuildVersionsCollection();
    }

    private void RebuildVersionsCollection()
    {
        Versions.Clear();

        foreach (var releaseView in releasesFeed.GetAllVersions().Select(gv => new GameVersionViewModel(gv)))
        {
            Versions.Add(releaseView);
        }
    }
}