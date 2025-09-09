using System.Collections.ObjectModel;
using System.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Community;

public partial class CommunityTabViewModel(ObservableCollection<NewsItemViewModel> newsCollection) : ViewModelBase
{
    public NewsCollectionViewModel NewsCollectionViewModel { get; set; } = new(newsCollection);

    public int SelectedNewsId
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedNews));
            }
        }
    } = -1;

    public News SelectedNews => News.GetNews(SelectedNewsId);
}