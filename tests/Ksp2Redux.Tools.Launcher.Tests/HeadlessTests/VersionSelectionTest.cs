using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
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
    [TestCase("stable", null, "0.2.8.5.103184", false, "v0.2.8.5.103184")]
    [TestCase("stable", null, "0.2.8.5.103184", true, "v0.2.8.5.103184")]
    [TestCase("beta", "26w41a", "0.2.8.5.103184", false, "26w41a")]
    [TestCase("beta", null, "0.2.8.5.103184", false, "v0.2.8.5.103184-snapshot")]
    [TestCase("stable", null, "0.2.9.0.104880", false, "v0.2.8.5.103184-snapshot")]
    [TestCase(null, null, "0.2.8.5.103184", false, "v0.2.8.5.103184-snapshot")]
    public void InstalledVersion_UsesMatchingPublishedMetadata(
        string? publishedChannel, string? label, string publishedVersion, bool publishToBothChannels,
        string expectedDisplay)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        const string INSTALL_DIR = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2";
        TestAppBuilder.FileSystem.Directory.CreateDirectory($@"{INSTALL_DIR}\Redux");
        TestAppBuilder.FileSystem.File.WriteAllText($@"{INSTALL_DIR}\Redux\manifest.json", "{}");
        TestAppBuilder.ModuleDefinitionService
            .Setup(m => m.ReadModule($@"{INSTALL_DIR}\KSP2_x64_Data\Managed\Assembly-CSharp.dll"))
            .Returns(() => TestHelpers.GenerateMockVersionID(
                ("VERSION_TEXT", "0.2.8.5.103184"),
                ("CHANNEL_NAME", "beta"),
                ("DEBUG_INFO", "ffc94930")).module);

        var patch = new ReleasePatch
        {
            ChecksumSha256 = "0",
            ReleasedAt = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Requires = new PatchRequirement { Version = null },
            Size = 10,
            Url = "https://example.com/patch",
            Version = publishedVersion,
            Label = label
        };
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.IsAny<FeedInfo>()))
            .ReturnsAsync((FeedInfo feed) => new ReleaseManifest
            {
                Channel = feed.Filename.Split('-', '.')[1],
                GeneratedAt = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
                Patches = publishToBothChannels || feed.Filename == $"manifest-{publishedChannel}.json" ? [patch] : [],
                SchemaVersion = 1
            });

        // Feed order must not make a promoted build keep its snapshot suffix.
        TestAppBuilder.ServiceProvider.GetRequiredService<ILauncherConfigService>().Config.Feeds.Reverse();
        var window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var versionSelector = window.GetVisualDescendants()
            .OfType<GroupedComboBox>()
            .Single(control => control.Name == "VersionSelector");
        var installedVersion = versionSelector.GroupedItems
            .OfType<GameVersionViewModel>()
            .Single(version => version.Channel == "installed");
        var installService = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();

        Assert.Multiple(() =>
        {
            Assert.That(installedVersion.VersionString, Is.EqualTo(expectedDisplay));
            Assert.That(installService.Ksp2?.GameVersion?.Channel, Is.EqualTo("beta"),
                "Display metadata must not overwrite the detected installation metadata.");
        });
    }

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
            Version = "0.2.3.1.1234"
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
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
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
