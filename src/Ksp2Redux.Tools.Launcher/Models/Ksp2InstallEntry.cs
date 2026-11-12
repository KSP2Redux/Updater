namespace Ksp2Redux.Tools.Launcher.Models;

public class Ksp2InstallEntry
{
    /// <summary>
    /// Gets or sets the explicitly selected original stock identity, or null for exact local matching.
    /// </summary>
    public string? FullSourceBaselineIdentity { get; set; }

    /// <summary>
    /// Gets or sets explicit target-file exclusions retained without initial ownership.
    /// </summary>
    public List<string> FullSourceRetainedUnownedFiles { get; set; } = [];

    /// <summary>
    /// Gets or sets explicit preexisting directory exclusions retained without ownership.
    /// </summary>
    public List<string> FullSourceUnownedDirectories { get; set; } = [];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ReleaseChannel { get; set; } = "beta";
    public GameVersion? LastInstalledVersion { get; set; }
    public bool LaunchThroughSteam { get; set; } = false;
    public string SteamAppId { get; set; } = "954850";
    public string LaunchArguments { get; set; } = "";
    public bool DisableGraphicsJobs { get; set; } = false;
}
