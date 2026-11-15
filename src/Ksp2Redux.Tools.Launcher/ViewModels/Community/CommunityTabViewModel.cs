using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels.Shared;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Community;

public partial class CommunityTabViewModel(INewsService newsService, IMessageBoxService messageBoxService, ILogService log) : ViewModelBase
{
    public Task LaunchExternalLinkAsync(TopLevel? topLevel, string? url)
        => ExternalLinkLauncher.LaunchAsync(topLevel, url, messageBoxService, log);

    /// <summary>The id of the post the Community tab shows, or null when none is open.</summary>
    public string? SelectedNewsId
    {
        get;
        private set
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