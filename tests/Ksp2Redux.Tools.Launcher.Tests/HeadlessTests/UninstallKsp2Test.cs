using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
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
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

// Settings could install KSP2 and install or uninstall Redux, but getting rid of the game itself meant
// finding and deleting the folder by hand.
public class UninstallKsp2Test
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";
    private const string EXE = GAME + @"\KSP2_x64.exe";

    private static (MainWindow Window, SettingsTabViewModel Settings) StartWithStandaloneInstall(ButtonResult confirm)
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(true);
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                "Uninstall KSP2", It.IsAny<string>(), ButtonEnum.YesNo, It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()))
            .ReturnsAsync(confirm);

        var fileSystem = TestAppBuilder.FileSystem;
        fileSystem.Directory.CreateDirectory(GAME + @"\KSP2_x64_Data");
        fileSystem.File.Copy(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe", EXE);

        var main = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var installs = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        installs.SetActiveInstall(installs.AddInstall(EXE, "Standalone").Id);
        main.CurrentTab = MainWindowViewModel.SettingsTabId;
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        return (window, TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>());
    }

    [AvaloniaTest]
    public async Task UninstallKsp2_Confirmed_DeletesTheGameAndItsProfile()
    {
        // Arrange
        var (_, settings) = StartWithStandaloneInstall(ButtonResult.Yes);
        Assert.That(settings.SelectedInstall?.Name, Is.EqualTo("Standalone"));

        // Act
        await settings.UninstallKsp2Command.ExecuteAsync(null);

        // Assert
        Assert.That(TestAppBuilder.FileSystem.Directory.Exists(GAME), Is.False);
        var installs = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        Assert.That(installs.Entries.Select(e => e.ExePath), Does.Not.Contain(EXE));
        Assert.That(settings.IsUninstallingKsp2, Is.False);
    }

    [AvaloniaTest]
    public async Task UninstallKsp2_Declined_LeavesEverything()
    {
        // Arrange
        var (_, settings) = StartWithStandaloneInstall(ButtonResult.No);

        // Act
        await settings.UninstallKsp2Command.ExecuteAsync(null);

        // Assert
        Assert.That(TestAppBuilder.FileSystem.File.Exists(EXE), Is.True);
        Assert.That(settings.SelectedInstall?.Name, Is.EqualTo("Standalone"));
    }

    [AvaloniaTest]
    public async Task UninstallKsp2_NotAGameFolder_DeletesNothing()
    {
        // Arrange
        var (_, settings) = StartWithStandaloneInstall(ButtonResult.Yes);
        TestAppBuilder.FileSystem.Directory.Delete(GAME + @"\KSP2_x64_Data");

        // Act
        await settings.UninstallKsp2Command.ExecuteAsync(null);

        // Assert
        Assert.That(TestAppBuilder.FileSystem.File.Exists(EXE), Is.True);
        Assert.That(settings.SelectedInstall?.Name, Is.EqualTo("Standalone"));
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "Can't Uninstall KSP2", It.IsAny<string>(), It.IsAny<ButtonEnum>(), Icon.Warning, It.IsAny<object>(), It.IsAny<WindowStartupLocation>()), Times.Once);
    }

    // Three buttons now share the row, so none of their labels may be cut off at the narrowest window.
    [AvaloniaTest]
    public void ActionButtons_AtMinimumWindowWidth_ShowTheirWholeLabel()
    {
        // Arrange
        var (window, _) = StartWithStandaloneInstall(ButtonResult.No);
        window.Width = window.MinWidth;
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        // Act
        var view = window.GetVisualDescendants().OfType<SettingsTabView>().Single();
        var buttons = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is "Install Patch File" or "Uninstall Redux" or "Uninstall KSP2").ToList();

        // Assert
        Assert.That(buttons, Has.Count.EqualTo(3));
        foreach (var button in buttons)
        {
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            label.Measure(Size.Infinity);
            var available = button.Bounds.Width - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right;
            Assert.That(label.DesiredSize.Width, Is.LessThanOrEqualTo(available), $"\"{button.Content}\" is cut off");
        }
    }
}
