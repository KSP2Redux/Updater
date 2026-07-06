using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test.HeadlessTests;

public class UnknownGameVersionTest
{
    [AvaloniaTest]
    public void UpdateMainButtonState_GameVersionCouldNotBeDetected_DisablesTheMainButton()
    {
        // Arrange - IsValid only confirms the exe exists; it says nothing about whether we could
        // read its version. A stock install whose VersionID type is missing an expected field (a
        // game update that changed field names, an unsupported distribution, etc.) used to leave
        // Install/Update enabled with an unknown "from" version - see issue #26.
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();

        // Override the module set up by MockKsp2StockSteamInstall with one whose VersionID type is
        // missing VERSION_TEXT, so FromVersionIDType throws and detection fails.
        var brokenModule = TestHelpers.GenerateMockVersionID(("DEBUG_INFO", "BUILD_INFO"));
        TestAppBuilder.ModuleDefinitionService
            .Setup(m => m.ReadModule(
                @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Managed\Assembly-CSharp.dll"))
            .Returns(brokenModule.module);

        // Act
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var homeTabViewModel = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(homeTabViewModel.MainButtonEnabled, Is.False,
                "Install/Update/Launch must not be reachable when the current game version is unknown.");
            Assert.That(homeTabViewModel.MainButtonTooltip, Does.Contain("Couldn't detect the installed game version"));
        });
    }
}
