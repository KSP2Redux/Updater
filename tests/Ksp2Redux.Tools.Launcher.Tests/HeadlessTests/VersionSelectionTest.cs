using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class VersionSelectionTest
{
    private const string DefaultChannel = "beta";
    private const string OtherChannel = "stable";

    [AvaloniaTest]
    public void ChangingAnInstallSetting_DoesNotResetTheSelectedVersion()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        ReleasePatch patch = new()
        {
            ChecksumSha256 = "0",
            ReleasedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Requires = new PatchRequirement { Version = null },
            Size = 10,
            Url = "https://github.com/patch1Rollup.patch",
            Version = "0.2.3.1.1234",
        };

        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel))))
            .ReturnsAsync(new ReleaseManifest
            {
                Channel = DefaultChannel,
                GeneratedAt = new DateTime(2020, 1, 4),
                Patches = [patch],
                SchemaVersion = 1
            });
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename.Contains(DefaultChannel) == false)))
            .ReturnsAsync((FeedInfo f) => new ReleaseManifest
            {
                Channel = f.Filename.Split('-', '.')[1],
                GeneratedAt = new DateTime(2020, 1, 4),
                Patches = [],
                SchemaVersion = 1
            });

        // Act
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        GroupedComboBox? versionSelectorCombobox = window
            .GetVisualDescendants()
            .OfType<GroupedComboBox>()
            .FirstOrDefault(x => x.Name == "VersionSelector");
        Assert.That(versionSelectorCombobox, Is.Not.Null);

        var nonDefaultVersion = versionSelectorCombobox.GroupedItems
            .OfType<GameVersionViewModel>()
            .Single(g => g.VersionString.Contains("0.2.3.1.1234"));
        versionSelectorCombobox.SelectedItem = nonDefaultVersion;
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();
        Assert.That(homeTabViewModel.SelectedVersion?.VersionString, Does.Contain("0.2.3.1.1234"));

        // Simulate a settings-page change to the active install (e.g. toggling "disable graphics jobs"),
        // which fires ActiveInstallChanged - this used to silently reset the version dropdown.
        var ksp2InstallService = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        var activeEntryId = ksp2InstallService.ActiveEntry!.Id;
        ksp2InstallService.UpdateInstallDisableGraphicsJobs(activeEntryId, true);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Assert.That(homeTabViewModel.SelectedVersion?.VersionString, Does.Contain("0.2.3.1.1234"));
    }
}
