using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public class GameVersionViewModel(GameVersion gameVersion) : ViewModelBase, IGroupedComboBoxItem
{
    public string Channel = gameVersion.Channel;
    public string VersionString => $"v{gameVersion.VersionNumber}.{gameVersion.BuildNumber}" +
                                   $"{(gameVersion.Channel == "stable"
                                       ? ""
                                       : $"-{gameVersion.Channel.ToLower()}")}";

    public string ReleaseDateString => gameVersion.ReleasedAt is { } d
        ? d.ToLocalTime().ToString("yyyy-MM-dd")
        : string.Empty;

    public bool HasReleaseDate => gameVersion.ReleasedAt is not null;

    public bool IsSelectable => true;

    public GameVersion Version => gameVersion;

    public bool Equals(GameVersion? other)
    {
        return gameVersion.Equals(other);
    }
}