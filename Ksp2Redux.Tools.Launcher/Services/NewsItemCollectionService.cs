using System.Collections.ObjectModel;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface INewsItemCollectionService
{
    ObservableCollection<NewsItemViewModel> NewsCollection { get; set; }
    void Add(NewsItemViewModel newsItemViewModel);
}

public class NewsItemCollectionService : INewsItemCollectionService
{
    public ObservableCollection<NewsItemViewModel> NewsCollection { get; set; } = [];

    public void Add(NewsItemViewModel newsItemViewModel)
    {
        NewsCollection.Add(newsItemViewModel);
    }
}