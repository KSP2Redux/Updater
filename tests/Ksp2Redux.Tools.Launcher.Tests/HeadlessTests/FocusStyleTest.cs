using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class FocusStyleTest
{
    // Fluent fills a keyboard-focused ComboBox with the accent, a heavy red block here, inside the shared
    // focus ring. Its selector beats a plain one, so this checks the override really wins.
    [AvaloniaTest]
    public void KeyboardFocusedComboBox_ShowsNoAccentFill()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        var window = new MainWindow { DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        var combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "InnerComboBox");

        combo.Focus(NavigationMethod.Tab);
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        Assert.That(combo.Classes, Does.Contain(":focus-visible"));
        Assert.That(combo.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HighlightBackground").IsVisible, Is.False);
    }
}
