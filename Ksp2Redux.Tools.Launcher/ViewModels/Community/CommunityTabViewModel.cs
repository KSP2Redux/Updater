using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Community;

public partial class CommunityTabViewModel(INewsService newsService, IMessageBoxService messageBoxService, ILogService log) : ViewModelBase
{
    public Task LaunchExternalLinkAsync(TopLevel? topLevel, string? url)
        => ExternalLinkLauncher.LaunchAsync(topLevel, url, messageBoxService, log);

    private string? SelectedNewsId
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
    }

    // Falls back to an empty News record if the selected post no longer exists (e.g. the feed was
    // refetched and dropped it) instead of throwing or showing stale content.
    public NewsItemViewModel SelectedNews => new(newsService.GetNews(SelectedNewsId) ?? new());

    public bool NewsVisible => SelectedNewsId is not null;

    public void SetSelectedNewsId(string? newsId)
    {
        SelectedNewsId = newsId;
    }

    [RelayCommand]
    private void DeselectNews()
    {
        SelectedNewsId = null;
    }
}