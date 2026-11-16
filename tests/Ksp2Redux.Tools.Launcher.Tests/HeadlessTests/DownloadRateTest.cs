using Avalonia.Headless.NUnit;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Microsoft.Extensions.DependencyInjection;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class DownloadRateTest
{
    private const long MEGABYTE = 1024 * 1024;

    [AvaloniaTest]
    public async Task UpdateDownloadRate_ShowsSpeedAndTimeLeftOnlyWhileDownloading()
    {
        var home = TestAppBuilder.ServiceProvider.GetRequiredService<HomeTabViewModel>();

        home.UpdateDownloadRate(0, 1000 * MEGABYTE);
        home.UpdateDownloadRate(400 * MEGABYTE, 1000 * MEGABYTE);
        Assert.That(home.DownloadRateText, Is.Null, "A cached patch counted at the start is not a download speed.");

        await Task.Delay(TimeSpan.FromSeconds(1.1));
        home.UpdateDownloadRate(400 * MEGABYTE, 1000 * MEGABYTE);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        home.UpdateDownloadRate(410 * MEGABYTE, 1000 * MEGABYTE);
        Assert.That(home.DownloadRateText, Does.Match(@"/s · about \d+ (s|min) left$"));

        home.UpdateDownloadRate(1000 * MEGABYTE, 1000 * MEGABYTE);
        Assert.That(home.DownloadRateText, Is.Null);
    }
}
