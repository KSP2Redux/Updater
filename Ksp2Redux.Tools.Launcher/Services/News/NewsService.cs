using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.News;

public interface INewsService
{
    Task<List<Models.News>> FindAllNews();
    Models.News? GetNews(string? id);
    Task FetchNews();
}

public class NewsService(INewsProviderService newsProviderService, ILogService log) : INewsService
{
    private List<Models.News> _newsList = new();

    public async Task<List<Models.News>> FindAllNews() => await Task.Run(() => _newsList.OrderByDescending(n => n.Date).ToList());

    public Models.News? GetNews(string? id) => id is null ? null : _newsList.FirstOrDefault(n => n.Id == id);

    public async Task FetchNews()
    {
        // string tomlNewsContent = await newsProviderService.GetTomlContent();
        // LoadNewsFromToml(tomlNewsContent);
        var rssNewsContent = await newsProviderService.GetSyndicationFeed();
        LoadNewsFromFeed(rssNewsContent);
    }

    private void LoadNewsFromFeed(Feed rssNewsContent)
    {
        var newsList = new List<Models.News>();
        foreach (var item in rssNewsContent.Items)
        {
            if (item.PublishingDate is not { } date)
            {
                log.Warn($"Skipping RSS item \"{item.Title}\" - missing or unparseable publish date.");
                continue;
            }

            newsList.Add(new Models.News
            {
                Id = string.IsNullOrWhiteSpace(item.Link) ? Guid.NewGuid().ToString() : item.Link,
                Title = item.Title,
                Author = item.Author,
                Content = item.Content,
                Link = item.Link,
                Date = date,
            });
        }
        _newsList = newsList.OrderBy(n => n.Date).ToList();
    }
}