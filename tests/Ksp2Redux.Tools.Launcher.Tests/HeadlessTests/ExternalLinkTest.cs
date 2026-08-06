using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class ExternalLinkTest
{
    private static async Task<(MainWindowViewModel MainWindowViewModel, TopLevel TopLevel)> BootstrapAsync()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        var mainWindowViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        MainWindow window = new MainWindow { DataContext = mainWindowViewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (mainWindowViewModel, TopLevel.GetTopLevel(window)!);
    }

    [AvaloniaTest]
    public async Task LaunchExternalLinkAsync_MalformedUrl_ShowsFeedbackInsteadOfThrowing()
    {
        // Arrange
        var (mainWindowViewModel, topLevel) = await BootstrapAsync();

        // Act
        Exception? thrown = null;
        try
        {
            await mainWindowViewModel.LaunchExternalLinkAsync(topLevel, "not a valid url");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // Assert
        Assert.That(thrown, Is.Null, "A malformed link must not throw from the click handler.");
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Couldn't Open Link", It.IsAny<string>(),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }

    [AvaloniaTest]
    public async Task LaunchExternalLinkAsync_RelativeUrl_ShowsFeedbackInsteadOfThrowing()
    {
        // Arrange - a link that fails Uri.TryCreate's absolute-URI requirement (e.g. a feed
        // returning a site-relative path instead of a full URL).
        var (mainWindowViewModel, topLevel) = await BootstrapAsync();

        // Act
        Exception? thrown = null;
        try
        {
            await mainWindowViewModel.LaunchExternalLinkAsync(topLevel, "/blog/post-1");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // Assert
        Assert.That(thrown, Is.Null);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Couldn't Open Link", It.IsAny<string>(),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }

    [AvaloniaTest]
    public async Task CommunityTab_LaunchExternalLinkAsync_MalformedUrl_ShowsFeedbackInsteadOfThrowing()
    {
        // Arrange
        var (_, topLevel) = await BootstrapAsync();
        var communityTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<CommunityTabViewModel>();

        // Act - an empty/malformed link, e.g. a news post with no Link, used to reach `new Uri("")`.
        Exception? thrown = null;
        try
        {
            await communityTabViewModel.LaunchExternalLinkAsync(topLevel, "");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // Assert
        Assert.That(thrown, Is.Null);
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
                "Couldn't Open Link", It.IsAny<string>(),
                It.IsAny<ButtonEnum>(), It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()),
            Times.Once);
    }
}
