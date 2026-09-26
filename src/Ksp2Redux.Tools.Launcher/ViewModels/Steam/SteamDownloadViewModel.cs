using System.Diagnostics;
using System.IO.Abstractions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Steam;

/// <summary>
/// Drives the window that downloads KSP2 from Steam into a folder and registers it as an install.
/// </summary>
public partial class SteamDownloadViewModel : ViewModelBase
{
    private readonly ISteamDepotDownloader _downloader;
    private readonly IKsp2InstallService _installService;
    private readonly IFileSystem _fileSystem;
    private readonly ILogService _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _clock = new();
    private readonly TransferRateMeter _rate = new();
    private TimeSpan? _lastDetailRefresh;

    private static readonly TimeSpan DETAIL_REFRESH_INTERVAL = TimeSpan.FromMilliseconds(100);

    public SteamDownloadViewModel(string targetDirectory, ISteamDepotDownloader downloader, IKsp2InstallService installService,
        IFileSystem fileSystem, ILogService log)
    {
        TargetDirectory = targetDirectory;
        _downloader = downloader;
        _installService = installService;
        _fileSystem = fileSystem;
        _log = log;
    }

    /// <summary>Raised when the window should close, with the new install's exe path when the download finished.</summary>
    public event EventHandler<string?>? Completed;

    public string TargetDirectory { get; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string StageText { get; set; } = "Connecting to Steam";

    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseButtonText))]
    public partial bool IsRunning { get; set; } = true;

    public string CloseButtonText => IsRunning ? "Cancel" : "Close";

    /// <summary>
    /// Downloads KSP2 into <see cref="TargetDirectory"/>, registers it as the active install and raises
    /// <see cref="Completed"/>. Failures are reported through <see cref="ErrorMessage"/>, not thrown.
    /// </summary>
    public async Task StartAsync()
    {
        var progress = new Progress<SteamDownloadProgress>(OnProgress);
        _clock.Start();
        try
        {
            await _downloader.DownloadAsync(SteamDepotDownloader.KSP2_APP_ID, TargetDirectory, progress, _cancellation.Token);

            var exePath = _fileSystem.Path.Combine(TargetDirectory, Ksp2Install.KSP2_EXE_NAME);
            if (!_fileSystem.File.Exists(exePath))
            {
                throw new InvalidDataException($"The download finished but {Ksp2Install.KSP2_EXE_NAME} is missing from it.");
            }

            var existing = _installService.Entries.FirstOrDefault(e => string.Equals(e.ExePath, exePath, StringComparison.Ordinal));
            if (existing is null)
            {
                _installService.AddInstall(exePath, "KSP2 (Steam download)");
            }
            else
            {
                _installService.SetActiveInstall(existing.Id);
            }

            Completed?.Invoke(this, exePath);
        }
        catch (OperationCanceledException)
        {
            Completed?.Invoke(this, null);
        }
        catch (Exception ex)
        {
            _log.Error("Downloading KSP2 from Steam failed.", ex);
            ErrorMessage = ex.Message;
            StageText = "Download stopped";
            IsRunning = false;
        }
    }

    /// <summary>Cancels a running download, or closes the window once it has stopped.</summary>
    [RelayCommand]
    public void Close()
    {
        if (IsRunning)
        {
            _cancellation.Cancel();
            return;
        }

        Completed?.Invoke(this, null);
    }

    private void OnProgress(SteamDownloadProgress progress)
    {
        Dispatcher.UIThread.VerifyAccess();
        StageText = progress.Stage switch
        {
            "Downloading" => "Downloading Kerbal Space Program 2",
            "Finished" => "Download complete",
            _ => progress.Stage
        };

        if (progress.TotalBytes <= 0)
        {
            IsIndeterminate = true;
            return;
        }

        IsIndeterminate = false;
        ProgressPercent = 100.0 * progress.DownloadedBytes / progress.TotalBytes;

        var now = _clock.Elapsed;
        _rate.Sample(progress.DownloadedBytes, now);

        var finished = progress.Stage == "Finished";
        if (!finished && _lastDetailRefresh is { } last && now - last < DETAIL_REFRESH_INTERVAL) return;
        _lastDetailRefresh = now;

        DetailText = FormatDetail(progress, _rate.BytesPerSecond);
    }

    internal static string FormatDetail(SteamDownloadProgress progress, double? bytesPerSecond)
    {
        var speed = bytesPerSecond is { } rate ? $"{FormatBytes((long)Math.Max(0, rate))}/s" : "measuring...";
        return $"{FormatBytes(progress.DownloadedBytes)} of {FormatBytes(progress.TotalBytes)}  ·  {speed}  ·  " +
               $"{progress.FilesDone}/{progress.FilesTotal} files";
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }
}
