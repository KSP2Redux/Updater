using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Community;

public partial class CommunityTabViewModel(INewsItemCollectionService newsCollectionService, INewsService newsService) : ViewModelBase
{
    public NewsCollectionViewModel NewsCollectionViewModel { get; set; } = new(newsCollectionService.NewsCollection);

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

    public NewsItemViewModel SelectedNews => new(newsService, newsService.GetNews(SelectedNewsId));
    
    public bool NewsVisible => SelectedNewsId != -1;
    
    public void SetSelectedNewsId(int newsId)
    {
        SelectedNewsId = newsId;
    }
}