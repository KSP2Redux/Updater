using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class DiagnosticInfoTest
{
    [AvaloniaTest]
    public async Task BuildDiagnosticInfo_ActiveInstallAndLogPresent_BundlesEverythingIntoOneBlock()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>()
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var settingsTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        TestAppBuilder.ServiceProvider.GetRequiredService<ILogService>().Info("marker line for the diagnostic tail");

        // Act
        string diagnosticInfo = settingsTabViewModel.BuildDiagnosticInfo();

        // Assert
        Assert.That(diagnosticInfo, Does.Contain("KSP2 Redux Launcher v"));
        Assert.That(diagnosticInfo, Does.Contain("Active install:"));
        Assert.That(diagnosticInfo, Does.Contain("--- Recent log ---"));
        Assert.That(diagnosticInfo, Does.Contain("marker line for the diagnostic tail"),
            "The launcher keeps its log open for writing, so the tail has to be read with a share mode that allows that.");
    }

    [AvaloniaTest]
    public void BuildDiagnosticInfo_ReportsTheSetupWithoutPrivateValues()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(true);
        TestAppBuilder.WineRuntimeService.Setup(w => w.Detect()).Returns(
            new WineRuntime(WineRuntimeKind.Bundled, "Wine 10", "/Applications/KSP2 Redux.app/wine", "/Applications/KSP2 Redux.app", "/prefix"));
        TestAppBuilder.WineRuntimeService.Setup(w => w.IsRosettaInstalled()).Returns(false);
        var installs = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        var install = installs.AddInstall(@"C:\Games\KSP2\KSP2_x64.exe", "Main");
        installs.SetActiveInstall(install.Id);
        install.LaunchArguments = "-secret-token abc123";

        var settingsTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        // Act
        string diagnosticInfo = settingsTabViewModel.BuildDiagnosticInfo();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(diagnosticInfo, Does.Contain("launcher process"));
            Assert.That(diagnosticInfo, Does.Contain("Culture:"));
            Assert.That(diagnosticInfo, Does.Contain("Download cache:"));
            Assert.That(diagnosticInfo, Does.Contain("Steam: not signed in"));
            Assert.That(diagnosticInfo, Does.Contain("Feeds:"));
            Assert.That(diagnosticInfo, Does.Contain("Wine runtime: Bundled (Wine 10)"));
            Assert.That(diagnosticInfo, Does.Contain("Rosetta: missing"));
            Assert.That(diagnosticInfo, Does.Contain("* Main [beta] launch=direct, graphicsJobs=on, launchArguments=set, free="));
            Assert.That(diagnosticInfo, Does.Not.Contain("abc123"), "Launch arguments can hold anything, so only whether they are set is reported.");
        });
    }

    [AvaloniaTest]
    public void BuildDiagnosticInfo_GameRunning_IncludesItsPlayerLogAndHidesTheHomeFolder()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(false);
        TestAppBuilder.EnvironmentProvider.SetFolderPath(Environment.SpecialFolder.UserProfile, @"C:\Users\Jebediah Kerman");
        var fileSystem = TestAppBuilder.FileSystem;
        var gameData = @"C:\Users\Jebediah Kerman\AppData\LocalLow\Intercept Games\Kerbal Space Program 2";
        fileSystem.Directory.CreateDirectory(gameData);
        using var game = fileSystem.FileStream.New(fileSystem.Path.Combine(gameData, "Player.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        using (var writer = new StreamWriter(game, leaveOpen: true))
        {
            writer.WriteLine("NullReferenceException in the flight scene");
            writer.WriteLine("Loading C:/Users/Jebediah Kerman/AppData/LocalLow/Intercept Games/Kerbal Space Program 2/Saves/quicksave.json");
        }

        var settingsTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        // Act
        string diagnosticInfo = settingsTabViewModel.BuildDiagnosticInfo();

        // Assert
        Assert.That(diagnosticInfo, Does.Contain("NullReferenceException in the flight scene"));
        Assert.That(diagnosticInfo, Does.Contain(@"Game data folder: ~\AppData\LocalLow"));
        Assert.That(diagnosticInfo, Does.Not.Contain("Jebediah"));
    }

    [AvaloniaTest]
    public void CopyDiagnosticInfoButton_ReportFails_KeepsTheLauncherRunning()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        var main = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        window.Show();
        main.CurrentTab = MainWindowViewModel.SettingsTabId;
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        TestAppBuilder.DiskSpaceService.Setup(d => d.GetAvailableFreeSpace(It.IsAny<string>()))
            .Throws(new IOException("drive went away"));
        var copy = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "Copy Diagnostic Info"));

        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.That(() => { for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); }, Throws.Nothing);
    }
}
