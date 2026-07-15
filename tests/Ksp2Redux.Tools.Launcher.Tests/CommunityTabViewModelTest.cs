using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests;

public class CommunityTabViewModelTest
{
    [Test]
    public void SelectedNews_IdNoLongerInTheBackingList_FallsBackToEmptyInsteadOfThrowing()
    {
        var newsService = new Mock<INewsService>();
        newsService.Setup(n => n.GetNews("stale-id")).Returns((News?)null);
        var communityTabViewModel = new CommunityTabViewModel(newsService.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);

        communityTabViewModel.SetSelectedNewsId("stale-id");

        Assert.That(communityTabViewModel.SelectedNews.Title, Is.EqualTo(string.Empty));
    }

    [Test]
    public void NewsVisible_NothingSelected_IsFalse()
    {
        var newsService = new Mock<INewsService>();
        var communityTabViewModel = new CommunityTabViewModel(newsService.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);

        Assert.That(communityTabViewModel.NewsVisible, Is.False);
    }

    [Test]
    public void SetSelectedNewsId_MatchingId_ShowsThatArticle()
    {
        var news = new News { Id = "abc", Title = "Hello" };
        var newsService = new Mock<INewsService>();
        newsService.Setup(n => n.GetNews("abc")).Returns(news);
        var communityTabViewModel = new CommunityTabViewModel(newsService.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);

        communityTabViewModel.SetSelectedNewsId("abc");

        Assert.Multiple(() =>
        {
            Assert.That(communityTabViewModel.NewsVisible, Is.True);
            Assert.That(communityTabViewModel.SelectedNews.Title, Is.EqualTo("Hello"));
        });
    }
}
