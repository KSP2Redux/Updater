using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    public MainWindowViewModel()
    {
        Config = LauncherConfig.GetOrCreateCurrentConfig();

        HomeTab = new HomeTabViewModel(NewsCollection);
        CommunityTab = new CommunityTabViewModel(NewsCollection);
        ModsTab = new ModsTabViewModel();
        SettingsTab = new SettingsTabViewModel(Config);

        LoadNews();
        TryLoadKsp2Install();
    }

    private async void LoadNews()
    {
        List<News> newsList = await News.FindAllNews();
        foreach (News news in newsList)
        {
            NewsCollection.Add(new Shared.NewsItemViewModel(news));
        }
    }

    private void TryLoadKsp2Install()
    {
        if (!string.IsNullOrWhiteSpace(Config.Ksp2InstallPath))
        {
            Ksp2 = new(Config.Ksp2InstallPath);
        }
    }
}