using System.Diagnostics;
using System.IO.Abstractions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Localization;
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
    private readonly ILocalizationService _localization;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _clock = new();
    private readonly TransferRateMeter _rate = new();
    private TimeSpan? _lastDetailRefresh;
    private string _stage = CONNECTING_STAGE;

    private static readonly TimeSpan DETAIL_REFRESH_INTERVAL = TimeSpan.FromMilliseconds(100);

    // The downloader's own stage names, plus the two this window adds.
    private const string CONNECTING_STAGE = "Connecting";
    private const string STOPPED_STAGE = "Stopped";

    public SteamDownloadViewModel(string targetDirectory, ISteamDepotDownloader downloader, IKsp2InstallService installService,
        IFileSystem fileSystem, ILogService log, ILocalizationService localization)
    {
        TargetDirectory = targetDirectory;
        _downloader = downloader;
        _installService = installService;
        _fileSystem = fileSystem;
        _log = log;
        _localization = localization;
        SetStage(CONNECTING_STAGE);
        _localization.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>Raised when the window should close, with the new install's exe path when the download finished.</summary>
    public event EventHandler<string?>? Completed;

    public string TargetDirectory { get; }

    public string TargetDirectoryText => _localization.Format("Steam.Download.Target", TargetDirectory);

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string StageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseButtonText))]
    public partial bool IsRunning { get; set; } = true;

    public string CloseButtonText => _localization[IsRunning ? "Common.Cancel" : "Common.Close"];

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
                throw _localization.Message("Steam.Download.MissingExe", Ksp2Install.KSP2_EXE_NAME)
                    .ToException(message => new InvalidDataException(message));
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
            ErrorMessage = ex.UserMessage();
            SetStage(STOPPED_STAGE);
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
        SetStage(progress.Stage);

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

    private void SetStage(string stage)
    {
        _stage = stage;
        StageText = stage switch
        {
            CONNECTING_STAGE => _localization["Steam.Download.Stage.Connecting"],
            "Downloading" => _localization["Steam.Download.Stage.Downloading"],
            "Finished" => _localization["Steam.Download.Stage.Finished"],
            SteamDepotDownloader.STAGE_CHECKING => _localization["Steam.Download.Stage.Checking"],
            SteamDepotDownloader.STAGE_READING => _localization["Steam.Download.Stage.Reading"],
            STOPPED_STAGE => _localization["Steam.Download.Stage.Stopped"],
            _ => stage
        };
    }

    // The progress line is worded again with the next progress report.
    private void OnLanguageChanged()
    {
        SetStage(_stage);
        OnPropertyChanged(nameof(TargetDirectoryText));
        OnPropertyChanged(nameof(CloseButtonText));
    }

    internal static string FormatDetail(SteamDownloadProgress progress, double? bytesPerSecond)
    {
        // Static, so it reads the shared service instead of the injected one.
        var localization = LocalizationService.Default;
        var speed = bytesPerSecond is { } rate
            ? localization.Format("Steam.Download.Speed", FormatBytes((long)Math.Max(0, rate)))
            : localization["Steam.Download.Measuring"];
        return localization.Format("Steam.Download.Detail", FormatBytes(progress.DownloadedBytes),
            FormatBytes(progress.TotalBytes), speed, progress.FilesDone, progress.FilesTotal);
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
