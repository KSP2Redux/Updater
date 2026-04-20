using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Launcher.Models;
using Tomlyn;
using Tomlyn.Model;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface INewsService
{
    Task<List<News>> FindAllNews();
    News GetNews(int id);
    int GetNewsId(News? news);
    void LoadNewsFromToml(string tomlContent);
    Task<MemoryStream?> LoadImageStreamAsync(News news);
    Task FetchNews();
}

public class NewsService(INewsProviderService newsProviderService) : INewsService
{
    private List<News> _newsList = new();
    
    public async Task<List<News>> FindAllNews() => await Task.Run(() => _newsList.OrderByDescending(n => n.Date).ToList());

    public News GetNews(int id) => id == -1 ? new News() : _newsList[id];
    
    public int GetNewsId(News? news) => news == null ? -1 : _newsList.IndexOf(news);

    public async Task FetchNews()
    {
        string tomlNewsContent = await newsProviderService.GetTomlContent();
        LoadNewsFromToml(tomlNewsContent);
    }
    
    public void LoadNewsFromToml(string tomlContent)
    {
        var model = Toml.ToModel(tomlContent);
        var newsArray = (TomlTableArray)model["news"];

        _newsList = newsArray
            .Select(entry => new News
            {
                Title = (string)entry["Title"],
                Content = (string)entry["Content"],
                Date = Convert.ToDateTime(entry["Date"]),
                Author = (string)entry["Author"],
                ImageUrl = entry.TryGetValue("ImageUrl", out var image) ? (string)image : null,
                Link = entry.TryGetValue("Link", out var link) ? (string)link : "",
            })
            .ToList();
    }
    
    public async Task<MemoryStream?> LoadImageStreamAsync(News news)
    {
        byte[] data = new byte[1];
        try
        {
            data = await newsProviderService.GetImageData(news.ImageUrl);
            return new MemoryStream(data);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Couldn't load image: {e}");
        }
        return null;
    }
}