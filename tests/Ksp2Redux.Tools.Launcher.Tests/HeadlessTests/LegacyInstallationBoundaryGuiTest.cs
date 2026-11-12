using System.Diagnostics;
using System.IO.Compression;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

/// <summary>
/// Tests the production GUI boundary with actual cache and install services over disposable mock roots.
/// </summary>
public class LegacyInstallationBoundaryGuiTest
{
    private const string INSTALL_ROOT = @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2";

    private static void Bootstrap(bool pending, bool fullArchive)
    {
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();
        TestAppBuilder.UpdateService.Setup(service => service.CheckAndPerformUpdateAsync()).ReturnsAsync(true);
        TestAppBuilder.NewsProviderService.Setup(service => service.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        if (fullArchive)
        {
            using Stream input = TestAppBuilder.FileSystem.File.Create(TestAppBuilder.FileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip"));
            using var archive = new ZipArchive(input, ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry(StockBaselineManifest.MANIFEST_PATH).Open());
            writer.Write("invalid metadata still protects");
        }
        if (pending)
            TestAppBuilder.FileSystem.File.WriteAllText(TestAppBuilder.FileSystem.Path.Combine(INSTALL_ROOT, ".redux-transaction.active"), "");
        TestAppBuilder.FileSystem.File.WriteAllText(TestAppBuilder.FileSystem.Path.Combine(INSTALL_ROOT, "unknown.txt"), "unowned bytes");
        TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>()
            .AddInstall(TestAppBuilder.FileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe"));
        TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        Dispatcher.UIThread.RunJobs();
    }

    private static Dictionary<string, byte[]> Snapshot() =>
        TestAppBuilder.FileSystem.Directory.EnumerateFiles(INSTALL_ROOT, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, TestAppBuilder.FileSystem.File.ReadAllBytes);

    private static async Task AssertBlockedLaunchAsync(bool macOS, bool throughSteam)
    {
        Bootstrap(true, false);
        var install = TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>();
        install.ActiveEntry!.LaunchThroughSteam = throughSteam;
        TestAppBuilder.OperatingSystemService.Setup(service => service.IsMacOS()).Returns(macOS);
        var before = Snapshot();
        var home = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        await home.LaunchGame();

        TestAppBuilder.GameProcessService.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        TestAppBuilder.WineRuntimeService.Verify(service => service.Detect(), Times.Never);
        Assert.That(Snapshot(), Is.EqualTo(before));
        Assert.That(TestAppBuilder.MessageBoxService.Invocations.Any(call =>
            call.Arguments.OfType<string>().Any(text => text.Contains("unfinished installation transaction"))), Is.True);
    }

    /// <summary>
    /// Pending state prevents starting the native game process.
    /// </summary>
    /// <returns>The completed test.</returns>
    [AvaloniaTest]
    public Task PendingTransactionBlocksDirectGuiLaunch() => AssertBlockedLaunchAsync(false, false);

    /// <summary>
    /// Pending state prevents opening Steam's game-launch URL.
    /// </summary>
    /// <returns>The completed test.</returns>
    [AvaloniaTest]
    public Task PendingTransactionBlocksSteamGuiLaunch() => AssertBlockedLaunchAsync(false, true);

    /// <summary>
    /// Pending state prevents Wine detection, preparation and launch.
    /// </summary>
    /// <returns>The completed test.</returns>
    [AvaloniaTest]
    public Task PendingTransactionBlocksWineGuiLaunch() => AssertBlockedLaunchAsync(true, true);

    /// <summary>
    /// A completed full archive is preserved through Settings' direct Redux restore path.
    /// </summary>
    /// <returns>The completed test.</returns>
    [AvaloniaTest]
    public async Task DirectSettingsRestorePreservesFullArchiveAndUnknownFiles()
    {
        Bootstrap(false, true);
        var before = Snapshot();
        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();

        await settings.UninstallRedux();

        Assert.That(Snapshot(), Is.EqualTo(before));
        Assert.That(TestAppBuilder.MessageBoxService.Invocations.Any(call =>
            call.Arguments.OfType<string>().Any(text => text.Contains("claims a full stock backup"))), Is.True);
        TestAppBuilder.ZipFileService.Verify(service => service.OpenRead(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A pending marker survives Settings' direct Redux restore path.
    /// </summary>
    /// <returns>The completed test.</returns>
    [AvaloniaTest]
    public async Task DirectSettingsRestorePreservesPendingMarker()
    {
        Bootstrap(true, true);
        var before = Snapshot();

        await TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>().UninstallRedux();

        Assert.That(Snapshot(), Is.EqualTo(before));
        Assert.That(TestAppBuilder.MessageBoxService.Invocations.Any(call =>
            call.Arguments.OfType<string>().Any(text => text.Contains("unfinished installation transaction"))), Is.True);
    }

    /// <summary>
    /// A completed full archive permits the ordinary GUI process launch.
    /// </summary>
    /// <returns>The completed test.</returns>
    [AvaloniaTest]
    public async Task CompletedFullArchivePermitsGuiLaunch()
    {
        Bootstrap(false, true);
        TestAppBuilder.ServiceProvider.GetRequiredService<IKsp2InstallService>().ActiveEntry!.LaunchThroughSteam = false;
        TestAppBuilder.GameProcessService.Setup(service => service.WaitForStartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var before = Snapshot();

        await TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>().LaunchGame();

        TestAppBuilder.GameProcessService.Verify(service => service.Start(It.IsAny<ProcessStartInfo>()), Times.Once);
        Assert.That(Snapshot(), Is.EqualTo(before));
    }
}
