using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Launcher.Models;

public class LauncherConfig
{
    public string Ksp2InstallPath { get; set; } = "";
    public string ReleaseChannel { get; set; }
    public GameVersion? LastInstalledVersion { get; set; }
    public List<FeedInfo> Feeds { get; set; } = [];

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
