using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public partial class NewsItemViewModel : ViewModelBase
{
    public News News { get; set; }
    
    public string Title => News.Title;

    public string Content => News.Content;

    public DateTime Date => News.Date;

    public string Author => News.Author;

    [ObservableProperty] public partial Bitmap? Image { get; private set; }

    public string? Link => News.Link;

    public string Subtitle => $"{News.Date:d} by {News.Author}";

    public async Task LoadImageAsync()
    {
        if (string.IsNullOrEmpty(News.ImageUrl))
        {
            return;
        }

        await using Stream stream = await News.LoadImageStreamAsync();
        Image = await Task.Run(() => Bitmap.DecodeToWidth(stream, 800));
    }

    public NewsItemViewModel(News news)
    {
        News = news;
    }
}