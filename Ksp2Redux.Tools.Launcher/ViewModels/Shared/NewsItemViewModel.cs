using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public class NewsItemViewModel(News news) : ViewModelBase
{
    public News News { get; set; } = news;

    public string Title => News.Title;

    public string Content => News.Content;

    public DateTime Date => News.Date;

    public string Author => News.Author;

    public Task<Bitmap?> Image => LoadImageAsync();

    public string? Link => News.Link;

    public string Subtitle => $"{News.Date:d} by {News.Author}";
    
    public bool ImageVisible { get; private set; }

    private async Task<Bitmap?> LoadImageAsync()
    {
        if (string.IsNullOrEmpty(News.ImageUrl))
        {
            ImageVisible = false;
            OnPropertyChanged(nameof(ImageVisible));
            return null;
        }

        await using var stream = await News.LoadImageStreamAsync();
        if (stream == null)
        {
            ImageVisible = false;
            OnPropertyChanged(nameof(ImageVisible));
            return null;
        }

        ImageVisible = true;
        OnPropertyChanged(nameof(ImageVisible));
        return await Task.Run(() => new Bitmap(stream));
    }
}