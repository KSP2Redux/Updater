using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public partial class HomeTabViewModel : ViewModelBase
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; }

    public ObservableCollection<GameVersionViewModel> Versions { get; }

    [ObservableProperty] public partial GameVersionViewModel? SelectedVersion { get; set; }

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => (item as GameVersionViewModel)?.Channel ?? string.Empty;

    public HomeTabViewModel(ObservableCollection<NewsItemViewModel> newsCollection)
    {
        NewsCollection = newsCollection;

        Versions = new ObservableCollection<GameVersionViewModel>(
            new List<GameVersion>
            {
                new() { VersionNumber = new("0.2.2.0"), BuildNumber = "32914", Channel = ReleaseChannel.Stable },
                new() { VersionNumber = new("0.2.3.0"), BuildNumber = "101291", Channel = ReleaseChannel.Stable },
                new() { VersionNumber = new("0.2.4.0"), BuildNumber = "103456", Channel = ReleaseChannel.Beta },
            }.Select(gv => new GameVersionViewModel(gv))
        );
    }
}