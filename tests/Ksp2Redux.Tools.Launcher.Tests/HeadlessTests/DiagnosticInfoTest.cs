using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
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
        using (var writer = new StreamWriter(game, leaveOpen: true)) writer.WriteLine("NullReferenceException in the flight scene");

        var settingsTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        // Act
        string diagnosticInfo = settingsTabViewModel.BuildDiagnosticInfo();

        // Assert
        Assert.That(diagnosticInfo, Does.Contain("NullReferenceException in the flight scene"));
        Assert.That(diagnosticInfo, Does.Contain(@"Game data folder: ~\AppData\LocalLow"));
        Assert.That(diagnosticInfo, Does.Not.Contain("Jebediah"));
    }
}
