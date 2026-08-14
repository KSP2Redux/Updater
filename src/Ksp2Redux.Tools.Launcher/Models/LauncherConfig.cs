using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Launcher.Models;

public class LauncherConfig(string storagePath)
{
    public const string PUBLIC_REPOSITORY = "https://github.com/KSP2Redux/Redux";
    public const string R2_MANIFEST_BASE_URL = "https://download.ksp2redux.org";
    public const string GITHUB_MANIFEST_BASE_URL = "https://raw.githubusercontent.com/KSP2Redux/Redux/main";

    public string Ksp2InstallPath { get; set; } = "";
    public string ReleaseChannel { get; set; } = "beta";
    public bool AutoSwitchedToStable { get; set; } = false;
    public bool LaunchThroughSteam { get; set; } = false;
    public string SteamAppId { get; set; } = "954850";
    public string LaunchArguments { get; set; } = "-popupwindow";
    public GameVersion? LastInstalledVersion { get; set; }
    public List<Ksp2InstallEntry> Ksp2Installs { get; set; } = [];
    public Guid? ActiveKsp2InstallId { get; set; }
    public List<FeedInfo> Feeds { get; set; } = [
        new FeedInfo
        {
            Repository = PUBLIC_REPOSITORY,
            Filename = "manifest-stable.json",
            R2ManifestUrl = $"{R2_MANIFEST_BASE_URL}/manifest-stable.json",
            GitHubManifestUrl = $"{GITHUB_MANIFEST_BASE_URL}/manifest-stable.json"
        },
        new FeedInfo
        {
            Repository = PUBLIC_REPOSITORY,
            Filename = "manifest-beta.json",
            R2ManifestUrl = $"{R2_MANIFEST_BASE_URL}/manifest-beta.json",
            GitHubManifestUrl = $"{GITHUB_MANIFEST_BASE_URL}/manifest-beta.json"
        }
    ];

    public PatchDownloadSource PatchDownloadSource { get; set; } = PatchDownloadSource.R2;
    public int MaxConcurrentChunkDownloads { get; set; } = 4;

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
    public string StoragePath { get; set; } = storagePath;

    public LauncherConfig() : this(string.Empty)
    {
    }
}
