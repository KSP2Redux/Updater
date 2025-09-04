using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public class GameVersionViewModel(GameVersion gameVersion) : ViewModelBase, IGroupedComboBoxItem
{
    public string Channel => gameVersion.Channel.ToString();
    public string VersionString => $"v{gameVersion.VersionNumber}.{gameVersion.BuildNumber}" +
                                   $"{(gameVersion.Channel == ReleaseChannel.Stable
                                       ? ""
                                       : $"-{gameVersion.Channel.ToString().ToLower()}")}";

    public bool IsSelectable => true;

    public GameVersion Version => gameVersion;

    public bool Equals(GameVersion? other)
    {
        return gameVersion.Equals(other);
    }
}