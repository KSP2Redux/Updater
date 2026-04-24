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
    public GameVersion? LastInstalledVersion { get; set; }
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
