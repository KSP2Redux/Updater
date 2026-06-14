using System.Text;
using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public class GameVersionViewModel(GameVersion gameVersion) : ViewModelBase, IGroupedComboBoxItem
{
    public string Channel = gameVersion.Channel;
    public string VersionString => new StringBuilder()
        .Append('v')
        .Append(gameVersion.VersionNumber)
        .Append('.')
        .Append(gameVersion.BuildNumber)
        .Append(gameVersion.Channel == "stable"
            ? string.Empty
            : new StringBuilder()
                .Append('-')
                .Append(gameVersion.Channel.ToLower()).ToString())
        .ToString();

    public string ReleaseDateString => gameVersion.ReleasedAt is { } dateTime
        ? dateTime.ToLocalTime().ToString("yyyy-MM-dd")
        : string.Empty;

    public bool HasReleaseDate => gameVersion.ReleasedAt is not null;

    public bool IsSelectable => true;

    public GameVersion Version => gameVersion;

    public bool Equals(GameVersion? other)
    {
        return gameVersion.Equals(other);
    }
}