using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Tomlyn;
using Tomlyn.Model;

namespace Ksp2Redux.Tools.Launcher.Models;

public class News
{
    private static readonly string _rawRepoTargetUrl = "https://raw.githubusercontent.com/SamBret/LauncherNews/refs/heads/main/";
    private static readonly string _tomlTargetFile = "news.toml";
    private static string _tomlTargetUrl => _rawRepoTargetUrl + _tomlTargetFile;
    private static string _imageTargetUrl => _rawRepoTargetUrl + "images/";

    private static readonly HttpClient _httpClient = new();
    
    private static List<News> _newsList = new();

    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Date { get; set; }
    public string Author { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string Link { get; set; } = "";

    public static async Task<List<News>> FindAllNews() => await Task.Run(() => _newsList.OrderByDescending(n => n.Date).ToList());

    public static News GetNews(int id) => id == -1 ? new News() : _newsList[id];
    
    public static int GetNewsId(News? news) => news == null ? -1 : _newsList.IndexOf(news);

    public async Task<MemoryStream?> LoadImageStreamAsync()
    {
        byte[] data = new byte[1];
        try
        {
            data = await _httpClient.GetByteArrayAsync(_imageTargetUrl + ImageUrl);
            return new MemoryStream(data);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Couldn't load image: {e}");
        }
        return null;
    }
    
    public static async Task<string> GetTomlContent()
    {
        return await _httpClient.GetStringAsync(_tomlTargetUrl);
    }
    
    public static void LoadNewsFromToml(string tomlContent)
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
}