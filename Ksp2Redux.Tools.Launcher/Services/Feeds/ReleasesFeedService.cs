using System.Collections.Generic;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

public interface IReleasesFeedService
{
    IReadOnlyDictionary<string, ManifestReleasesFeed> ReleasesFeed { get; }
    void AddOrSet(string channel, ManifestReleasesFeed releasesFeed);
}

public class ReleasesFeedService : IReleasesFeedService
{
    private Dictionary<string, ManifestReleasesFeed> _releasesFeed = [];
    public IReadOnlyDictionary<string, ManifestReleasesFeed> ReleasesFeed => _releasesFeed;

    public void AddOrSet(string channel, ManifestReleasesFeed releasesFeed)
    {
        _releasesFeed[channel] = releasesFeed;
    }
}