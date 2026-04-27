using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Settings;

public partial class Ksp2InstallRowViewModel : ViewModelBase
{
    private readonly IKsp2InstallService _ksp2InstallService;
    private readonly Ksp2InstallEntry _entry;

    public Guid Id => _entry.Id;

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string ExePath { get; set; }
    [ObservableProperty] public partial string ReleaseChannel { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool LaunchThroughSteam { get; set; }
    [ObservableProperty] public partial string SteamAppId { get; set; }
    [ObservableProperty] public partial string LaunchArguments { get; set; }
    [ObservableProperty] public partial bool DisableGraphicsJobs { get; set; }

    public Ksp2InstallRowViewModel(IKsp2InstallService ksp2InstallService, Ksp2InstallEntry entry, bool isActive)
    {
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
    }

    partial void OnNameChanged(string value) => _ksp2InstallService.RenameInstall(_entry.Id, value);
    partial void OnExePathChanged(string value) => _ksp2InstallService.UpdateInstallExePath(_entry.Id, value);
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
}
