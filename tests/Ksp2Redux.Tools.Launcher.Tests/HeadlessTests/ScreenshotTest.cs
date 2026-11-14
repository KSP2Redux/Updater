using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

/// <summary>
/// Renders the main window in a few states to PNGs for reviewing visual changes, each with a
/// <c>.layout.txt</c> listing where every named or classed control landed, in window coordinates.
/// A scenario ending in <c>@min</c> renders at the minimum window size.
/// Run with <c>LAUNCHER_SCREENSHOT_DIR=/some/folder dotnet test --filter ScreenshotTest</c>.
/// </summary>
[Explicit("Writes screenshots for design review; set LAUNCHER_SCREENSHOT_DIR.")]
public class ScreenshotTest
{
    private const int HOME = 0, COMMUNITY = 1, SETTINGS = 3;

    [AvaloniaTest]
    [TestCase("home")]
    [TestCase("home-installing")]
    [TestCase("home-installing-measuring")]
    [TestCase("home-installing@min")]
    [TestCase("home-installed")]
    [TestCase("home-failed")]
    [TestCase("community")]
    [TestCase("settings")]
    [TestCase("settings@min")]
    [TestCase("settings-bottom")]
    [TestCase("settings-hover")]
    [TestCase("tab-hover-HomeTab")]
    [TestCase("tab-hover-CommunityTab")]
    [TestCase("tab-hover-SettingsTab")]
    public void Render(string name)
    {
        var directory = new SystemEnvironmentProvider().GetEnvironmentVariable("LAUNCHER_SCREENSHOT_DIR");
        Assume.That(directory, Is.Not.Null.And.Not.Empty, "Set LAUNCHER_SCREENSHOT_DIR to where the PNGs should go.");

        var scenario = name.Split('@')[0];
        var (window, viewModel) = Start(minimumSize: name.EndsWith("@min"));
        switch (scenario)
        {
            case "home":
                break;
            case "home-installing":
            case "home-installing-measuring":
                var home = viewModel.HomeTab;
                home.IsInstallLogVisible = true;
                home.IsProgressVisible = true;
                home.Log(LogMessage.Literal("Creating an install plan"));
                home.Log(LogMessage.Literal("Downloading 3 patches (2.1 GB)"));
                home.Log(LogMessage.Literal("Verifying patch 1 of 3"));
                home.DownloadProgressTotalMb = 2150;
                home.DownloadProgressMb = 830;
                home.InstallProgressTotalSteps = 4;
                home.InstallProgressSteps = 1;
                home.DownloadRateText = scenario == "home-installing" ? "12.3 MB/s · about 2 min left" : null;
                break;
            case "home-installed":
            case "home-failed":
                var done = viewModel.HomeTab;
                done.IsInstallLogVisible = true;
                done.Log(LogMessage.Literal("Creating an install plan"));
                done.Log(LogMessage.Literal("Downloading 3 patches (2.1 GB)"));
                done.Log(LogMessage.Literal("Applying patch 1 of 3"));
                if (scenario == "home-installed")
                {
                    done.Log(LogMessage.Literal("Applying patch 2 of 3"));
                    done.Log(LogMessage.Literal("Applying patch 3 of 3"));
                    done.LogOutcome(LogMessage.Literal("KSP2 Redux Successfully Installed"), InstallLogKind.Success);
                }
                else
                {
                    done.LogError(LogMessage.Literal("Patch 1 failed: the archive is damaged. Your install was rolled back."));
                    done.LogOutcome(LogMessage.Literal("Nothing was changed."), InstallLogKind.Notice);
                }
                break;
            case "community":
                viewModel.CommunityTab.SetSelectedNewsId("https://ksp2redux.org/news/0-6-4");
                viewModel.CurrentTab = COMMUNITY;
                break;
            case "settings":
                viewModel.CurrentTab = SETTINGS;
                break;
            case "settings-bottom":
                viewModel.CurrentTab = SETTINGS;
                Pump();
                foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Extent.Height > s.Viewport.Height))
                {
                    scroller.Offset = new Vector(0, scroller.Extent.Height);
                }
                break;
            case var hover when hover.StartsWith("tab-hover-"):
                var tab = window.GetVisualDescendants().OfType<TabItem>().First(t => t.Name == hover["tab-hover-".Length..]);
                window.MouseMove(tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
                break;
            case "settings-hover":
                viewModel.CurrentTab = SETTINGS;
                Pump();
                var uninstall = window.GetVisualDescendants().OfType<Button>()
                    .First(button => button.Content is string text && text.Contains("Uninstall Redux"));
                var center = uninstall.TranslatePoint(new Point(uninstall.Bounds.Width / 2, uninstall.Bounds.Height / 2), window)!.Value;
                window.MouseMove(center, RawInputModifiers.None);
                break;
        }

        Pump();
        using (var layout = File.CreateText(Path.Combine(directory!, $"{name}.layout.txt")))
        {
            foreach (var control in window.GetVisualDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && (!string.IsNullOrEmpty(c.Name) || c.Classes.Count > 0)))
            {
                if (control.TranslatePoint(new Point(0, 0), window) is not { } origin) continue;
                layout.WriteLine($"{control.GetType().Name} {control.Name} [{string.Join(' ', control.Classes)}] x={origin.X:0.##} y={origin.Y:0.##} w={control.Bounds.Width:0.##} h={control.Bounds.Height:0.##}");
            }
        }
        window.CaptureRenderedFrame()!.Save(Path.Combine(directory!, $"{name}.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) Start(bool minimumSize)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed
        {
            Items =
            [
                new FeedItem { Link = "https://ksp2redux.org/news/0-6-4", Title = "Redux 0.6.4 is out", Content = "<h2>What is new</h2><p>Faster loading, the new launcher and a pile of fixes. This release brings the .NET 11 launcher, save backups before every install, and a download speed readout while patches download.</p><p>Read the full notes on the <a href=\"https://ksp2redux.org\">website</a>.</p>", PublishingDate = new DateTime(2026, 11, 14) },
                new FeedItem { Link = "https://ksp2redux.org/news/duna", Title = "Community spotlight: Duna landings", Content = "<p>Our favourite landings from the last month.</p>", PublishingDate = new DateTime(2026, 11, 2) },
            ]
        });
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.IsAny<FeedInfo>()))
            .ReturnsAsync((FeedInfo feed) => new ReleaseManifest
            {
                Channel = feed.Filename.Split('-', '.')[1],
                GeneratedAt = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
                Patches =
                [
                    new ReleasePatch
                    {
                        ChecksumSha256 = "0",
                        ReleasedAt = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc),
                        Requires = new PatchRequirement { Version = null },
                        Size = 2_150_000_000,
                        Url = "https://example.com/patch",
                        Version = "0.2.9.0.104880",
                    }
                ],
                SchemaVersion = 1
            });

        var viewModel = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        window.Width = minimumSize ? window.MinWidth : 1280;
        window.Height = minimumSize ? window.MinHeight : 800;
        window.Show();
        Pump();
        viewModel.CurrentTab = HOME;
        return (window, viewModel);
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++) Dispatcher.UIThread.RunJobs();
    }
}
