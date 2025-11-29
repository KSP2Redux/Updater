using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Community;

public partial class CommunityTabViewModel(ObservableCollection<NewsItemViewModel> newsCollection) : ViewModelBase
{
    public NewsCollectionViewModel NewsCollectionViewModel { get; set; } = new(newsCollection);

    private int SelectedNewsId
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedNews));
                OnPropertyChanged(nameof(NewsVisible));
            }
        }
    } = -1;

    public NewsItemViewModel SelectedNews => new (News.GetNews(SelectedNewsId));
    
    public bool NewsVisible => SelectedNewsId != -1;
    
    public void SetSelectedNewsId(int newsId)
    {
        SelectedNewsId = newsId;
    }
}