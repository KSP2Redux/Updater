using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public class NewsItemViewModel(News news) : ViewModelBase
{
    public News News { get; set; } = news;
    public string NewsId => News.Id;

    public string Title => News.Title;

    public string Content => News.Content;

    public DateTime Date => News.Date;

    public string Author => News.Author;

    public string? Link => News.Link;

    public string Subtitle => $"{News.Date:d}";
}