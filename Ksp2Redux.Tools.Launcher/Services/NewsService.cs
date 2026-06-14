using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface INewsService
{
    Task<List<News>> FindAllNews();
    News GetNews(int id);
    int GetNewsId(News? news);
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
        // string tomlNewsContent = await newsProviderService.GetTomlContent();
        // LoadNewsFromToml(tomlNewsContent);
        var rssNewsContent = await newsProviderService.GetSyndicationFeed();
        LoadNewsFromFeed(rssNewsContent);
    }

    private void LoadNewsFromFeed(Feed rssNewsContent)
    {
        _newsList = rssNewsContent.Items.Select(item => new News
            {
                Title = item.Title,
                Author = item.Author,
                Content = item.Content,
                Link = item.Link,
                Date = (DateTime)item.PublishingDate!
            }
        ).OrderBy(n => n.Date).ToList();
    }
}