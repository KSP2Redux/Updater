using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public partial class NewsItemViewModel(News news) : ViewModelBase
{
    public string Title => news.Title;

    public string Content => news.Content;

    public DateTime Date => news.Date;

    public string Author => news.Author;

    [ObservableProperty] public partial Bitmap? Image { get; private set; }

    public string? Link => news.Link;

    public string Subtitle => $"{news.Date:d} by {news.Author}";

    public async Task LoadImageAsync()
    {
        if (string.IsNullOrEmpty(news.ImageUrl))
        {
            return;
        }

        await using Stream stream = await news.LoadImageStreamAsync();
        Image = await Task.Run(() => Bitmap.DecodeToWidth(stream, 800));
    }
}