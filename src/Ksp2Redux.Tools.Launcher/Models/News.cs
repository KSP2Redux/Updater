namespace Ksp2Redux.Tools.Launcher.Models;

public sealed record News
{
    /// <summary>
    /// Stable identity for this post - defaults to its link (unique per RSS entry) so a post stays
    /// addressable across refetches, unlike a list index which shifts when the feed changes shape.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Link { get; init; } = string.Empty;
}
