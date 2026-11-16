using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class ModsEmptyStateTest
{
    [AvaloniaTest]
    public void ReadTheLatestNews_OpensCommunity()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        var viewModel = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        viewModel.CurrentTab = MainWindowViewModel.ModsTabId;
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Read the latest news");
        button.Command!.Execute(button.CommandParameter);
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        Assert.That(viewModel.CurrentTab, Is.EqualTo(MainWindowViewModel.CommunityTabId));
    }
}
