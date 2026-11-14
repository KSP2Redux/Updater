using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

public interface IReleasesFeedService
{
    IReadOnlyDictionary<string, ManifestReleasesFeed> ReleasesFeed { get; }
    IReadOnlyList<ManifestReleasesFeed> Feeds { get; }
    void Add(ManifestReleasesFeed releasesFeed);
}

public class ReleasesFeedService : IReleasesFeedService
{
    private readonly List<ManifestReleasesFeed> _feeds = [];

    public IReadOnlyList<ManifestReleasesFeed> Feeds => _feeds;

    public IReadOnlyDictionary<string, ManifestReleasesFeed> ReleasesFeed
    {
        get
        {
            Dictionary<string, ManifestReleasesFeed> byChannel = [];
            foreach (var feed in _feeds.Where(feed => feed.CurrentChannel != ManifestReleasesFeed.INVALID_CHANNEL))
            {
                byChannel[feed.CurrentChannel] = feed;
            }

            return byChannel;
        }
    }

    public void Add(ManifestReleasesFeed releasesFeed) => _feeds.Add(releasesFeed);
}
