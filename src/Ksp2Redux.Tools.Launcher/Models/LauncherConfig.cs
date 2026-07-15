using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Launcher.Models;

public class LauncherConfig
{
    public string Ksp2InstallPath { get; set; } = "";
    public string ReleaseChannel { get; set; } = "beta";
    public bool LaunchThroughSteam { get; set; } = false;
    public string SteamAppId { get; set; } = "954850";
    public string LaunchArguments { get; set; } = "-popupwindow";
    public GameVersion? LastInstalledVersion { get; set; }
    public List<Ksp2InstallEntry> Ksp2Installs { get; set; } = [];
    public Guid? ActiveKsp2InstallId { get; set; }
    public List<FeedInfo> Feeds { get; set; } = [
        new FeedInfo
        {
            Repository = "https://github.com/KSP2Redux/Redux",
            Filename ="manifest-stable.json"
        },
        new FeedInfo
        {
            Repository = "https://github.com/KSP2Redux/Redux",
            Filename ="manifest-beta.json"
        }
    ];

    public string LauncherRepo { get; set; } = "https://github.com/KSP2Redux/Updater";

    /// <summary>
    /// When enabled, lowers the log file's minimum level to Debug for more detailed troubleshooting output.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// Last window geometry. Null until the window has been closed once (first run
    /// uses the built-in defaults and centers on screen).
    /// </summary>
    public WindowPlacement? WindowPlacement { get; set; }

    [JsonIgnore]
    public string StoragePath { get; set; }

    public LauncherConfig() : this(string.Empty)
    {
    }

    public LauncherConfig(string storagePath)
    {
        StoragePath = storagePath;
    }
}
