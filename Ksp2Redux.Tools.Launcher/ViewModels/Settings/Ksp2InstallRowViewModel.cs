using System;
using System.IO.Abstractions;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Settings;

public partial class Ksp2InstallRowViewModel : ViewModelBase
{
    private readonly IFileSystem _fileSystem;
    private readonly IKsp2InstallService _ksp2InstallService;
    private readonly Ksp2InstallEntry _entry;

    public Guid Id => _entry.Id;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string ExePath { get; set; }

    [ObservableProperty]
    public partial string? ExePathError { get; set; }

    [ObservableProperty]
    public partial string ReleaseChannel { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool LaunchThroughSteam { get; set; }

    [ObservableProperty]
    public partial string SteamAppId { get; set; }

    [ObservableProperty]
    public partial string? SteamAppIdError { get; set; }

    [ObservableProperty]
    public partial string LaunchArguments { get; set; }

    [ObservableProperty]
    public partial bool DisableGraphicsJobs { get; set; }

    public Ksp2InstallRowViewModel(IFileSystem fileSystem, IKsp2InstallService ksp2InstallService, Ksp2InstallEntry entry, bool isActive)
    {
        _fileSystem = fileSystem;
        _ksp2InstallService = ksp2InstallService;
        _entry = entry;
        Name = entry.Name;
        ExePath = entry.ExePath;
        ReleaseChannel = entry.ReleaseChannel;
        IsActive = isActive;
        LaunchThroughSteam = entry.LaunchThroughSteam;
        SteamAppId = entry.SteamAppId;
        LaunchArguments = entry.LaunchArguments;
        DisableGraphicsJobs = entry.DisableGraphicsJobs;

        ExePathError = ValidateExePath(ExePath);
        SteamAppIdError = ValidateSteamAppId(SteamAppId);
    }

    partial void OnNameChanged(string value) => _ksp2InstallService.RenameInstall(_entry.Id, value);
    partial void OnExePathChanged(string value)
    {
        ExePathError = ValidateExePath(value);
        _ksp2InstallService.UpdateInstallExePath(_entry.Id, value);
    }
    partial void OnReleaseChannelChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _ksp2InstallService.UpdateInstallReleaseChannel(_entry.Id, value);
    }
    partial void OnIsActiveChanged(bool value)
    {
        if (value) _ksp2InstallService.SetActiveInstall(_entry.Id);
    }

    partial void OnLaunchThroughSteamChanged(bool value)
    {
        if (_entry.LaunchThroughSteam == value) return;
        _entry.LaunchThroughSteam = value;
        _ksp2InstallService.NotifyInstallChanged(_entry.Id);
    }

    partial void OnSteamAppIdChanged(string value)
    {
        var normalized = value?.Trim() ?? "";
        SteamAppIdError = ValidateSteamAppId(normalized);
        if (_entry.SteamAppId == normalized) return;
        _entry.SteamAppId = normalized;
        _ksp2InstallService.NotifyInstallChanged(_entry.Id);
    }

    partial void OnLaunchArgumentsChanged(string value)
    {
        var normalized = value ?? "";
        if (_entry.LaunchArguments == normalized) return;
        _entry.LaunchArguments = normalized;
        _ksp2InstallService.NotifyInstallChanged(_entry.Id);
    }

    partial void OnDisableGraphicsJobsChanged(bool value)
        => _ksp2InstallService.UpdateInstallDisableGraphicsJobs(_entry.Id, value);

    private string? ValidateExePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "An install path is required.";
        if (_fileSystem.Path.GetFileName(value) != Ksp2Install.KSP2_EXE_NAME) return $"Path must point to {Ksp2Install.KSP2_EXE_NAME}.";
        if (!_fileSystem.File.Exists(value)) return "That file doesn't exist.";
        return null;
    }

    private static string? ValidateSteamAppId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.All(char.IsAsciiDigit) ? null : "Steam App ID must be numeric.";
    }
}
