using System;

namespace Ksp2Redux.Tools.Launcher.Models;

public class Ksp2InstallEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ReleaseChannel { get; set; } = "beta";
    public GameVersion? LastInstalledVersion { get; set; }
    public bool LaunchThroughSteam { get; set; } = false;
    public string SteamAppId { get; set; } = "954850";
    public string LaunchArguments { get; set; } = "-popupwindow";
}
