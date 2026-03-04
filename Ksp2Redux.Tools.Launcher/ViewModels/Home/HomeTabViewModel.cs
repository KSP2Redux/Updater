using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;
using MsBox.Avalonia;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public partial class HomeTabViewModel : ViewModelBase
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; }
    
    public NewsCollectionViewModel NewsCollectionViewModel { get; set; }

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
    [ObservableProperty] public partial bool IsProgressVisible { get; private set; } = false;
    [ObservableProperty] public partial float DownloadProgressMb { get; private set; } = 250;
    [ObservableProperty] public partial float DownloadProgressTotalMb { get; private set; } = 450;
    [ObservableProperty] public partial float InstallProgressSteps { get; private set; } = 1;
    [ObservableProperty] public partial float InstallProgressTotalSteps { get; private set; } = 3;
    [ObservableProperty] public partial bool IsInstallLogVisible { get; private set; } = false;
    public ObservableCollection<LogItemViewModel> InstallLogLines { get; set; } = [];

    private readonly Dictionary<string,ManifestReleasesFeed> releasesFeed;
    private readonly MainWindowViewModel parentWindow;
    private CancellationTokenSource? cancelCurrentOperation;

    public static Func<object, string> GameVersionGroupKeySelector { get; } =
        item => (item as GameVersionViewModel)?.Channel ?? string.Empty;

    public HomeTabViewModel(MainWindowViewModel parentWindow)
    {
        NewsCollection = parentWindow.NewsCollection;
        NewsCollectionViewModel = new NewsCollectionViewModel(NewsCollection);
        releasesFeed = parentWindow.ReleasesFeed;
        this.parentWindow = parentWindow;
        RebuildVersionsCollection();
        PropertyChanged += ReactToPropertyChanges;
    }


    [RelayCommand]
    public async Task UpdateVersionsList(bool updateChannels = true)
    {
        if(updateChannels)
            UpdateAsync();
        RebuildVersionsCollection();
        UpdateMainButtonState();
    }
    private async void UpdateAsync()
    {
        foreach (var feed in releasesFeed)
        {
            await feed.Value.UpdateManifest();
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
    public async Task LaunchGame()
    {
        // Disable the main button while the game process is still running
        if (parentWindow.Ksp2 is not null)
        {
            MainButtonEnabled = false;
            using Process process = new();
            process.StartInfo.FileName = parentWindow.Ksp2.ExePath;
            process.StartInfo.WorkingDirectory = parentWindow.Ksp2.InstallDir;
            process.Start();
            await process.WaitForExitAsync();
            MainButtonEnabled = true;
        }
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
        parentWindow.TryLoadKsp2Install();
        var ksp2 = parentWindow.Ksp2;
        if (ksp2 is null || !ksp2.IsValid)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Launch;
            MainButtonTooltip = "KSP2 installation not detected.  Please select a directory containing KSP2 on the settings tab.";
            return;
        }
        
        
        
        var selectedVersion = SelectedVersion;
        if (selectedVersion is null)
        {
            MainButtonEnabled = false;
            MainButtonShown = MainButtonState.Install;
            MainButtonTooltip = "Please select a version to install or launch.";
            return;
        }
        
        if (ksp2.Distribution != Distribution.Redux)
        {
            MainButtonEnabled = true;
            if (selectedVersion.Version.Equals(ksp2.GameVersion) || selectedVersion.Channel == "installed")
            {
                MainButtonShown = MainButtonState.Launch;
                MainButtonTooltip = "Launch Stock KSP2";
            }
            else
            {
                MainButtonShown = MainButtonState.Install;
                MainButtonTooltip = "Install Ksp2Redux";
            }
            return;
        }

        if (selectedVersion.Version.Equals(ksp2.GameVersion) || selectedVersion.Channel == "installed")
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
        // We want to do this for the moment, we could fix this logic later at some point
        parentWindow.TryLoadKsp2Install();
        
        if (string.IsNullOrEmpty(parentWindow.Config.ReleaseChannel) || !releasesFeed.TryGetValue(parentWindow.Config.ReleaseChannel, out var value))
        {
            return;
        }
        Versions.Clear();

        if (parentWindow.Ksp2?.GameVersion != null)
        {
            var currentVersion = new GameVersionViewModel(parentWindow.Ksp2.GameVersion)
            {
                Channel = "installed"
            };
            Versions.Add(currentVersion);
            SelectedVersion = currentVersion;
            OnPropertyChanged(nameof(SelectedVersion));
        }
        
        // Select the correct version with the release view
        foreach (var releaseView in value.GetAllVersions().Select(gv => new GameVersionViewModel(gv)))
        {
            if (Versions.All(x => !(x.Version.Equals(releaseView.Version) && x.Channel.Equals(releaseView.Channel))))
            {
                Versions.Add(releaseView);
            }
        }
    }

    private async Task RunPatchProcess()
    {
        // // lock main window tabs?
        
        InstallLogLines.Clear();
        IsInstallLogVisible = true;
        IsProgressVisible = true;
        DownloadProgressMb = 0;
        DownloadProgressTotalMb = 0;
        InstallProgressSteps = 0;
        InstallProgressTotalSteps = 1;
        
        // gather process dependencies.
        
        parentWindow.TryLoadKsp2Install();
        var ksp2 = parentWindow.Ksp2;
        if (ksp2 is null || SelectedVersion is null)
        {
            return;
        }
        
        // Set up process cancellation trigger.
        MainButtonShown = MainButtonState.Cancel;
        MainButtonEnabled = true;
        MainButtonTooltip = "Cancel installation";
        cancelCurrentOperation = new CancellationTokenSource();
        var sb = new StringBuilder();

        Log("Creating install plan");
            
        var plan = parentWindow.ReleasesFeed[SelectedVersion.Channel]
            .GetPatchListToVersion(parentWindow.Ksp2!.GameVersion!, SelectedVersion.Version);
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log("KSP2 Redux Successfully Installed");
        }
        catch (Exception e)
        {
            Log($"Error updating Redux: {e.Message}");
            Log($"Stack Trace: {e.StackTrace}");
            Log($"Redux may be in an invalid state, try uninstalling and reinstalling");
        }
        finally
        {
            cancelCurrentOperation = null;
            parentWindow.TryLoadKsp2Install();
            await UpdateVersionsList();
        }
    }

    public async Task InstallFromPatchFile(string path)
    {
        
        InstallLogLines.Clear();
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
        
        parentWindow.TryLoadKsp2Install();
        var ksp2 = parentWindow.Ksp2;
        if (ksp2 is null || SelectedVersion is null)
        {
            return;
        }
        
        MainButtonShown = MainButtonState.Cancel;
        MainButtonEnabled = true;
        MainButtonTooltip = "Cancel installation";
        cancelCurrentOperation = new CancellationTokenSource();
        
        try
        {
            await RunPlanOnInstall(plan, ksp2);
            Log("KSP2 Redux Successfully Installed");
        }
        catch (Exception e)
        {
            Log($"Error updating Redux: {e.Message}");
            Log($"Stack Trace: {e.StackTrace}");
            Log($"Redux may be in an invalid state, try uninstalling and reinstalling");
        }
        finally
        {
            cancelCurrentOperation = null;
            parentWindow.TryLoadKsp2Install();
            await UpdateVersionsList();
        }
    }
    

    private async Task RunPlanOnInstall(InstallPlan plan, Ksp2Install ksp2)
    {
        UpdateStepsProgress(0, plan.Steps.Count);
        plan.Describe(Log);

        await plan.ApplyToFolder(ksp2.InstallDir, Log, UpdateDownloadProgress, UpdateStepsProgress,
            cancelCurrentOperation!.Token);

        void UpdateStepsProgress(int current, int max)
        {
            InstallProgressSteps = current;
            InstallProgressTotalSteps = max;
        }

        void UpdateDownloadProgress(long value, long max)
        {
            DownloadProgressMb = value / 1024f / 1024f;
            DownloadProgressTotalMb = max / 1024f / 1024f;
        }
    }

    private void Log(string message)
    {
        Console.WriteLine(message);
        Dispatcher.UIThread.Post(() =>
        {
            InstallLogLines.Add(new LogItemViewModel() { LogItemText = message });
        });
    }
}