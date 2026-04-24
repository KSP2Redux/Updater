using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public class NewsItemViewModel(INewsService newsService, News news) : ViewModelBase
{
    public News News { get; set; } = news;
    public int NewsId => newsService.GetNewsId(News);

    public string Title => News.Title;

    public string Content => News.Content;

    public DateTime Date => News.Date;

    public string Author => News.Author;

    public string? Link => News.Link;

    public string Subtitle => $"{News.Date:d}";
}