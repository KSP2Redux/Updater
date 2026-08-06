using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class WindowPlacementPersistenceTest
{
    [AvaloniaTest]
    public void ClosingTheWindow_PersistsItsPlacementToTheConfig()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        var configService = TestAppBuilder.ServiceProvider.GetRequiredService<ILauncherConfigService>();
        Assert.That(configService.Config.WindowPlacement, Is.Null, "Fresh config must start without a placement.");

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Width = 1400;
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var saved = configService.Config.WindowPlacement;
        Assert.Multiple(() =>
        {
            Assert.That(saved, Is.Not.Null, "Closing must write the placement to the config.");
            Assert.That(saved!.Width, Is.EqualTo(1400));
            Assert.That(saved.Height, Is.EqualTo(900));
            Assert.That(saved.IsMaximized, Is.False);
        });
    }
}
