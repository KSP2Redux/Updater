using System.Collections.ObjectModel;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Community;

public partial class CommunityTabViewModel(ObservableCollection<Shared.NewsItemViewModel> newsCollection) : ViewModelBase
{
    public ObservableCollection<Shared.NewsItemViewModel> NewsCollection { get; set; } = newsCollection;
}