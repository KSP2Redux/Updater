using System.Collections.ObjectModel;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public class NewsCollectionViewModel(ObservableCollection<NewsItemViewModel> newsCollection) : ViewModelBase
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; } = newsCollection;
}
