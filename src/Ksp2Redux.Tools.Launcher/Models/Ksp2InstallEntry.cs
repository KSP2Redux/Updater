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

    /// <summary>
    /// Gets or sets the boot.config settings of the release installed here, or null for none.
    /// </summary>
    public Dictionary<string, string>? BootConfigReleaseSettings { get; set; }

    /// <summary>
    /// Gets or sets the undo record of the boot.config settings step: each key it changed with the value the
    /// player had before, or null when the player had no such key.
    /// </summary>
    public Dictionary<string, string?> BootConfigOriginals { get; set; } = [];
}
