using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Shared;

public partial class NewsItemViewModel(News news) : ViewModelBase
{
    public News News { get; set; } = news;
    public string NewsId => News.Id;
    public string Title => News.Title;
    public string Content => News.Content;
    public DateTime Date => News.Date;
    public string Author => News.Author;
    public string? Link => News.Link;
    public string Subtitle => $"{News.Date:d}";

    /// <summary>The post's text without markup, for a short preview under the title.</summary>
    public string Excerpt { get; } = ToExcerpt(news.Content);

    /// <summary>Whether this is the post the Community tab shows.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // Headings repeat or label the title, so they are left out of the preview.
    internal static string ToExcerpt(string html)
    {
        var text = Heading().Replace(html, " ");
        // A block tag separates words; an inline one (a link, bold) sits inside a sentence.
        text = BlockTag().Replace(text, " ");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"<h[1-6]\b[^>]*>.*?</h[1-6]\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"</?(?:p|br|div|li|ul|ol|tr|td|blockquote)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
