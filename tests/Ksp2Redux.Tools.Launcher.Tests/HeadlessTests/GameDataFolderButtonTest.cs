using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class GameDataFolderButtonTest
{
    // A binding to a command that does not exist fails silently in Avalonia: the button renders and
    // does nothing at all when clicked. Asserting the command resolved is the difference between a
    // button that works and one that only looks like it does.
    [AvaloniaTest]
    public void SettingsTab_GameDataFolderButton_IsBoundToACommand()
    {
        // Arrange
        MainWindowViewModel model = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        MainWindow window = new() { DataContext = model };

        // Act
        window.Show();

        // The settings tab is only built once it is the current one.
        model.CurrentTab = MainWindowViewModel.SettingsTabId;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Button? button = window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "OpenGameDataFolderButton");

        // Assert
        Assert.That(button, Is.Not.Null, "the button is missing from the settings tab");
        Assert.That(button!.Command, Is.Not.Null, "the button is not bound to a command");
    }
}
