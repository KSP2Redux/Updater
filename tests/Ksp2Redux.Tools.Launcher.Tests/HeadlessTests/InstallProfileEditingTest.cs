using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Ksp2Redux.Tools.Launcher.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

// The profile name was a text box saving on every keystroke with no way to leave it, hidden settings rows
// left uneven gaps, and text fields had no way to commit short of clicking another control.
public class InstallProfileEditingTest
{
    private static (MainWindow Window, SettingsTabViewModel Settings) Start(bool isMacOS = true)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(isMacOS);
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();

        var main = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        main.CurrentTab = MainWindowViewModel.SettingsTabId;
        RunJobs();
        return (window, TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>());
    }

    private static void RunJobs()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static string ActiveEntryName() =>
        TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>().ActiveEntry!.Name;

    private static SettingsTabView SettingsView(MainWindow window) =>
        window.GetVisualDescendants().OfType<SettingsTabView>().Single();

    [AvaloniaTest]
    public void Rename_Committed_SavesTrimmedName()
    {
        // Arrange
        var (_, settings) = Start();
        settings.BeginRenameInstall();

        // Act
        settings.RenameText = "  Mac install  ";
        settings.CommitRenameInstall();

        // Assert
        Assert.That(settings.IsRenamingInstall, Is.False);
        Assert.That(settings.SelectedInstall!.Name, Is.EqualTo("Mac install"));
        Assert.That(ActiveEntryName(), Is.EqualTo("Mac install"));
    }

    [AvaloniaTest]
    public void Rename_WhileTyping_DoesNotSaveYet()
    {
        // Arrange
        var (_, settings) = Start();
        var original = ActiveEntryName();
        settings.BeginRenameInstall();

        // Act
        settings.RenameText = "Half typed";

        // Assert
        Assert.That(ActiveEntryName(), Is.EqualTo(original));
    }

    [AvaloniaTest]
    public void Rename_Blank_KeepsTheOldName()
    {
        // Arrange
        var (_, settings) = Start();
        var original = ActiveEntryName();
        settings.BeginRenameInstall();

        // Act
        settings.RenameText = "   ";
        settings.CommitRenameInstall();

        // Assert
        Assert.That(ActiveEntryName(), Is.EqualTo(original));
    }

    [AvaloniaTest]
    public void Rename_Cancelled_KeepsTheOldName()
    {
        // Arrange
        var (_, settings) = Start();
        var original = ActiveEntryName();
        settings.BeginRenameInstall();
        settings.RenameText = "Discarded";

        // Act
        settings.CancelRenameInstall();

        // Assert
        Assert.That(settings.IsRenamingInstall, Is.False);
        Assert.That(ActiveEntryName(), Is.EqualTo(original));
    }

    [AvaloniaTest]
    public void Rename_EnterInTheNameBox_Saves()
    {
        // Arrange
        var (window, settings) = Start();
        settings.BeginRenameInstall();
        RunJobs();
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RenameBox");
        Assert.That(box.IsFocused, Is.True);
        box.Text = "Typed name";

        // Act
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        RunJobs();

        // Assert
        Assert.That(settings.IsRenamingInstall, Is.False);
        Assert.That(ActiveEntryName(), Is.EqualTo("Typed name"));
    }

    [AvaloniaTest]
    public void Rename_EscapeInTheNameBox_CancelsAndStaysOnSettings()
    {
        // Arrange
        var (window, settings) = Start();
        var original = ActiveEntryName();
        settings.BeginRenameInstall();
        RunJobs();
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RenameBox").Text = "Discarded";

        // Act
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        RunJobs();

        // Assert
        Assert.That(settings.IsRenamingInstall, Is.False);
        Assert.That(ActiveEntryName(), Is.EqualTo(original));
        Assert.That(((MainWindowViewModel)window.DataContext!).CurrentTab, Is.EqualTo(MainWindowViewModel.SettingsTabId));
    }

    [AvaloniaTest]
    public void LaunchArguments_CommitOnEnterNotOnEveryKeystroke()
    {
        // Arrange
        var (window, settings) = Start();
        var box = window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.Text == settings.SelectedInstall!.LaunchArguments && t.IsVisible && t.Name != "RenameBox" && t.PlaceholderText is null);
        box.Focus();
        RunJobs();

        // Act
        box.Text = "-popupwindow -force-d3d11";
        var beforeEnter = settings.SelectedInstall!.LaunchArguments;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        RunJobs();

        // Assert
        Assert.That(beforeEnter, Is.EqualTo("-popupwindow"));
        Assert.That(box.IsFocused, Is.False);
        Assert.That(settings.SelectedInstall.LaunchArguments, Is.EqualTo("-popupwindow -force-d3d11"));
    }

    // Hidden rows (the Steam ones on macOS, an empty error line) used to still take up row spacing.
    [AvaloniaTest]
    public void FieldRows_WithHiddenRowsBetween_AreEvenlySpaced()
    {
        // Arrange
        var (window, _) = Start(isMacOS: true);

        // Act
        var rows = SettingsView(window).GetVisualDescendants().OfType<Grid>()
            .Where(g => g.Classes.Contains("field") && g.IsEffectivelyVisible && g.GetVisualParent() is StackPanel { Classes: var c } && c.Contains("form"))
            .Where(g => g.FindAncestorOfType<Border>() is { } border && border.Child is StackPanel panel && panel.Children.Contains(g))
            .ToList();
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(4));
        var panel = (StackPanel)rows[0].GetVisualParent()!;
        var gaps = new List<double>();
        for (var i = 1; i < rows.Count; i++)
        {
            var visibleBetween = panel.Children.SkipWhile(c => c != rows[i - 1]).Skip(1).TakeWhile(c => c != rows[i]).Any(c => c.IsVisible);
            if (visibleBetween) continue;
            gaps.Add(rows[i].Bounds.Top - rows[i - 1].Bounds.Bottom);
        }

        // Assert
        Assert.That(gaps, Is.Not.Empty);
        Assert.That(gaps, Is.All.EqualTo(panel.Spacing).Within(0.01));
    }

    // The label column is as wide as the longest label, and a long one pushed Download KSP2 onto a second line.
    [AvaloniaTest]
    public void ProfileButtons_AtMinimumWindowWidth_FitOnOneLine([Values(true, false)] bool isMacOS)
    {
        // Arrange
        var (window, _) = Start(isMacOS);

        // Act
        window.Width = window.MinWidth;
        RunJobs();

        // Assert
        var buttons = SettingsView(window).GetVisualDescendants().OfType<WrapPanel>().First().Children.OfType<Button>().ToList();
        Assert.That(buttons, Has.Count.EqualTo(4));
        Assert.That(buttons.Select(b => b.Bounds.Top).Distinct().Count(), Is.EqualTo(1));
    }
}
