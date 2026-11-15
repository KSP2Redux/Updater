using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class InstallLogViewTest
{
    private static (MainWindow Window, HomeTabViewModel Home) Start()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        var viewModel = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Pump();
        viewModel.HomeTab.IsInstallLogVisible = true;
        return (window, viewModel.HomeTab);
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static ListBox LogList(MainWindow window) =>
        window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "InstallLogList");

    [AvaloniaTest]
    public void Log_ShowsEachKindOfLineInItsOwnColour()
    {
        var (window, home) = Start();

        home.Log(LogMessage.Literal("Creating an install plan"));
        home.LogError(LogMessage.Literal("Patch failed"));
        home.LogOutcome(LogMessage.Literal("Installed"), InstallLogKind.Success);
        Pump();

        Assert.That(home.InstallLogEntries.Select(e => e.Kind),
            Is.EqualTo(new[] { InstallLogKind.Info, InstallLogKind.Error, InstallLogKind.Success }));
        Assert.That(home.InstallLogEntries.Select(e => e.Text), Is.EqualTo(new[] { "Creating an install plan", "Patch failed", "Installed" }));
        var lines = LogList(window).GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("log-line")).ToList();
        Assert.That(lines.Single(t => t.Text == "Patch failed").Foreground, Is.SameAs(window.FindResource("ErrorBrush")));
        Assert.That(lines.Single(t => t.Text == "✓ Installed").Foreground, Is.SameAs(window.FindResource("SuccessHoverBrush")));
    }

    [AvaloniaTest]
    public void NewOperation_ClearsTheLog()
    {
        var (window, home) = Start();
        home.Log(LogMessage.Literal("old line"));
        Pump();

        home.InstallLogEntries.Clear();
        Pump();

        Assert.That(LogList(window).ItemCount, Is.Zero);
    }

    // An install logs every file it touches, so a real log runs to tens of thousands of lines.
    [AvaloniaTest]
    public void TwentyThousandLines_OnlyTheLinesOnScreenAreBuilt()
    {
        var (window, home) = Start();
        for (var batch = 0; batch < 100; batch++)
        {
            for (var line = 0; line < 200; line++) home.Log(LogMessage.Literal($"Applying binary patch to KSP2_x64_Data/file{batch * 200 + line}.bundle"));
            Pump();
        }

        // A list that builds every line, or one text block holding the whole log, slows down as the log grows.
        // Counting built rows catches that without a timing bound, which shared CI runners miss at random.
        Assert.That(LogList(window).ItemCount, Is.EqualTo(20000));
        Assert.That(LogList(window).GetVisualDescendants().OfType<ListBoxItem>().Count(), Is.LessThan(200), "Only the lines on screen should exist.");
    }

    [AvaloniaTest]
    public async Task CtrlC_CopiesTheSelectedLinesInOrder()
    {
        var (window, home) = Start();
        home.Log(LogMessage.Literal("first"));
        home.Log(LogMessage.Literal("second"));
        home.Log(LogMessage.Literal("third"));
        Pump();
        var list = LogList(window);
        list.SelectedItems!.Add(home.InstallLogEntries[2]);
        list.SelectedItems.Add(home.InstallLogEntries[0]);
        // Keyboard focus in a list sits on an item, not on the list itself.
        list.ContainerFromIndex(0)!.Focus();

        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        Pump();

        var copied = await window.Clipboard!.TryGetTextAsync();
        Assert.That(copied, Does.Match(@"^\d\d:\d\d:\d\d  first\n\d\d:\d\d:\d\d  third$"));
    }

    [AvaloniaTest]
    public async Task CtrlC_AfterSelectingAWholeLongLog_CopiesItQuickly()
    {
        var (window, home) = Start();
        for (var line = 0; line < 20000; line++) home.Log(LogMessage.Literal($"Applying binary patch to file{line}.bundle"));
        Pump();
        var list = LogList(window);
        list.SelectAll();
        list.ScrollIntoView(0);
        Pump();
        list.ContainerFromIndex(0)!.Focus();

        var stopwatch = Stopwatch.StartNew();
        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        stopwatch.Stop();
        Pump();
        TestContext.Out.WriteLine($"Copying 20000 selected lines took {stopwatch.ElapsedMilliseconds}ms");

        var copied = await window.Clipboard!.TryGetTextAsync();
        Assert.That(copied!.Split('\n'), Has.Length.EqualTo(20000));
        Assert.That(copied, Does.EndWith("file19999.bundle"));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    // A list lets its items be as wide as they like unless horizontal scrolling is off, and then long
    // file paths are cut at the edge instead of wrapping.
    [AvaloniaTest]
    public void LongLine_AtMinimumWidth_Wraps()
    {
        var (window, home) = Start();
        window.Width = window.MinWidth;
        home.Log(LogMessage.Literal("Applying binary patch to KSP2_x64_Data/StreamingAssets/aa/StandaloneWindows64/a_very_long_bundle_name_that_cannot_fit_on_one_line_of_the_log.bundle"));
        Pump();

        var line = LogList(window).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("log-line"));
        Assert.That(line.Bounds.Height, Is.GreaterThan(30), "The line should wrap onto a second row.");
        Assert.That(line.Bounds.Width, Is.LessThanOrEqualTo(LogList(window).Bounds.Width));
    }
}
