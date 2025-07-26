using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public class GameVersionViewModel(GameVersion gameVersion) : ViewModelBase, ISelectableItem
{
    public string Channel => gameVersion.Channel.ToString();
    public string VersionString => $"v{gameVersion.VersionNumber}.{gameVersion.BuildNumber}" +
                                   $"{(gameVersion.Channel == ReleaseChannel.Beta ? "-beta" : "")}";

    public bool IsSelectable => true;
}