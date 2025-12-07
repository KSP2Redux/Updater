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
    [ObservableProperty] public partial float InstallProgressPatches { get; private set; } = 1;
    [ObservableProperty] public partial float InstallProgressTotalPatches { get; private set; } = 3;
    [ObservableProperty] public partial bool IsInstallLogVisible { get; private set; } = false;
    public ObservableCollection<LogItemViewModel> InstallLogLines { get; set; } = [];

    private readonly ManifestReleasesFeed releasesFeed;
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
    public async Task UpdateVersionsList()
    {
        UpdateAsync();
        RebuildVersionsCollection();
        UpdateMainButtonState();
    }
    private async void UpdateAsync()
    {
        await releasesFeed.UpdateManifest(releasesFeed.CurrentChannel);
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

        if (selectedVersion.Version.Equals(ksp2.GameVersion))
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

        InstallLogLines.Clear();
        IsInstallLogVisible = true;
        IsProgressVisible = true;
        DownloadProgressMb = 0;
        DownloadProgressTotalMb = 450;
        InstallProgressPatches = 0;
        InstallProgressTotalPatches = 1;

        // gather process dependencies.
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
        void log(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                InstallLogLines.Add(new LogItemViewModel() { LogItemText = message });
            });
        }
        void updateDownloadProgress(long value, long max)
        {
            DownloadProgressMb = value / 1024f / 1024f;
            DownloadProgressTotalMb = max / 1024f / 1024f;
        }

        try
        {
            //Get list of patches that need to be installed.
            List<ManifestReleasesFeed.Patch> patches;
            if (parentWindow.Ksp2.IsRedux)
            {
                patches = parentWindow.ReleasesFeed.GetPatchListToVersion(parentWindow.Ksp2.GameVersion,
                    SelectedVersion.Version);
            }
            else
            {
                string distribution = "";
                if (parentWindow.Ksp2.IsSteam)
                {
                    distribution = "steam";
                }
                else
                {
                    distribution = "portable";
                }

                patches = parentWindow.ReleasesFeed.GetPatchListToVersion(distribution, SelectedVersion.Version);
            }
            
            foreach (var patch in patches)
            {
                // Download the patch file.
                string downloadedFile = await parentWindow.ReleasesFeed.DownloadPatch(patch, log, updateDownloadProgress, cancelCurrentOperation.Token);
                
                // Run the patch installer.
                var patcher = Ksp2Patch.FromFile(downloadedFile);
                log($"Starting patch for {ksp2}\npatcher: {patcher}");
                string PatchFromDir = ksp2.InstallDir;
                string PatchToDir = parentWindow.Config.ReduxInstallPath;
                if (ksp2.IsRedux)
                {
                    if(Directory.Exists(parentWindow.Config.ReduxInstallPath + "old"))
                        Directory.Delete(parentWindow.Config.ReduxInstallPath + "old", true);
                    Directory.Move(parentWindow.Config.ReduxInstallPath,parentWindow.Config.ReduxInstallPath + "old");
                    Directory.CreateDirectory(PatchToDir);
                    PatchFromDir = parentWindow.Config.ReduxInstallPath + "old";
                }
                await patcher.AsyncApply(
                    PatchToDir,
                    PatchFromDir,
                    log, log
                );
                parentWindow.Config.Ksp2InstallPath = PatchToDir + "/KSP2_x64.exe";
                parentWindow.Config.Save();
                // update install model state if patch ran successfully.
                parentWindow.TryLoadKsp2Install();
            }
        }
        catch (Exception e)
        {
            log($"Error updating Redux: {e.Message}");
        }
        finally
        {
            cancelCurrentOperation = null;
            UpdateMainButtonState();
        }
    }
}