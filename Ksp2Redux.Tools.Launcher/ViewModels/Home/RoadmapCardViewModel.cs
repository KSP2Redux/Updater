namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

/// <summary>
/// One card in the Home hero's roadmap row. The roadmap is deliberately hardcoded in the
/// launcher for now - it changes rarely enough that shipping a release for it is fine.
/// </summary>
public class RoadmapCardViewModel(string title, string statusText, bool inProgress) : ViewModelBase
{
    public string Title => title;
    public string StatusText => statusText;
    public bool InProgress => inProgress;
}
