using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public partial class HomeTabViewModel : ViewModelBase
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; }

    public ObservableCollection<GameVersionViewModel> Versions { get; } = [];

    [ObservableProperty] public partial GameVersionViewModel? SelectedVersion { get; set; }


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

    private readonly GitHubReleasesFeed releasesFeed;
    private readonly MainWindowViewModel parentWindow;
    private CancellationTokenSource? cancelCurrentOperation;

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => (item as GameVersionViewModel)?.Channel ?? string.Empty;

    public HomeTabViewModel(MainWindowViewModel parentWindow)
    {
        NewsCollection = parentWindow.NewsCollection;
        releasesFeed = parentWindow.ReleasesFeed;
        this.parentWindow = parentWindow;
        RebuildVersionsCollection();
        PropertyChanged += ReactToPropertyChanges;
    }


    [RelayCommand]
    public async Task UpdateVersionsList()
    {
        await releasesFeed.UpdateFromApi();
        RebuildVersionsCollection();
        UpdateMainButtonState();
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
        //Process.Start
        // Disable the main button while the game process is still running
    }

    [RelayCommand]
    public void CancelCurrentMainButtonAction()
    {
        cancelCurrentOperation?.Cancel();
    }

    private void ReactToPropertyChanges(object? sender, PropertyChangedEventArgs? e)
    {
        if (e?.PropertyName == "SelectedVersion")
        {
            UpdateMainButtonState();
        }
    }

    private void UpdateMainButtonState()
    {
        var ksp2 = parentWindow.Ksp2;
        if (ksp2 is null || !ksp2.IsValid)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = "KSP2 installation not detected.  Please select a directory containing KSP2 on the settings tab.";
            return;
        }

        if (!ksp2.IsRedux)
        {
            MainButtonEnabled = true;
            MainButtonShown = MainButtonState.Install;
            MainButtonTooltip = "Install Ksp2Redux";
            return;
        }

        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Install;
            MainButtonTooltip = "Please select a version to install.";
            return;
        }

        if (selectedVersion.Equals(ksp2.GameVersion))
        {
            MainButtonEnabled = true;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = "Launch Ksp2Redux";
        }
        else
        {
            MainButtonEnabled = true;
            MainButtonShown = MainButtonState.Update;
            MainButtonTooltip = "Update Ksp2Redux";
        }
    }

    private void RebuildVersionsCollection()
    {
        Versions.Clear();

        foreach (var releaseView in releasesFeed.GetAllVersions().Select(gv => new GameVersionViewModel(gv)))
        {
            Versions.Add(releaseView);
        }
    }

    private async Task RunPatchProcess()
    {
        // lock main window tabs?

        // gather process dependencies.
        var ksp2 = parentWindow.Ksp2;
        if (ksp2 is null || SelectedVersion is null)
        {
            return;
        }

        // Set up process cancellation trigger.
        MainButtonShown = MainButtonState.Cancel;
        cancelCurrentOperation = new CancellationTokenSource();

        try
        {
            // Download the patch file.
            // TODO: Download progress bar updates.
            string downloadedFile = await parentWindow.ReleasesFeed.DownloadPatch(SelectedVersion.Version, ksp2.IsSteam, cancelCurrentOperation.Token);

            // TODO: Run the patch installer.

            // TODO: update install model state if patch ran successfully.
        }
        finally
        {
            cancelCurrentOperation = null;
            UpdateMainButtonState();
        }
    }
}