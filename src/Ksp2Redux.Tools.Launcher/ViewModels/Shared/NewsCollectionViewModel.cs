using System.Collections.ObjectModel;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public class NewsCollectionViewModel : ViewModelBase
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; }

    public NewsCollectionViewModel(ObservableCollection<NewsItemViewModel> newsCollection)
    {
        NewsCollection = newsCollection;
    }
}