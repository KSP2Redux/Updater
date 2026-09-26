using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Steam;

public class TransferRateMeterTest
{
    private const long MEGABYTE = 1024 * 1024;

    [Test]
    public void Sample_SteadyDownload_NeverDropsToZero()
    {
        // Arrange
        var meter = new TransferRateMeter();
        var lowest = double.MaxValue;

        // Act
        for (var ms = 0; ms <= 20_000; ms += 50)
        {
            meter.Sample(100 * MEGABYTE * ms / 1000, TimeSpan.FromMilliseconds(ms));
            if (ms >= 1000 && meter.BytesPerSecond is { } rate) lowest = Math.Min(lowest, rate);
        }

        // Assert
        Assert.That(lowest, Is.EqualTo(100.0 * MEGABYTE).Within(1).Percent);
    }

    [Test]
    public void Sample_BeforeFirstMeasurement_HasNoRate()
    {
        // Arrange
        var meter = new TransferRateMeter();

        // Act
        meter.Sample(0, TimeSpan.Zero);
        meter.Sample(MEGABYTE, TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.That(meter.BytesPerSecond, Is.Null);
    }

    [Test]
    public void Sample_DownloadStalls_RateEasesDown()
    {
        // Arrange
        var meter = new TransferRateMeter();
        meter.Sample(0, TimeSpan.Zero);
        meter.Sample(50 * MEGABYTE, TimeSpan.FromSeconds(1));
        var before = meter.BytesPerSecond!.Value;

        // Act
        meter.Sample(50 * MEGABYTE, TimeSpan.FromSeconds(2));

        // Assert
        Assert.That(meter.BytesPerSecond, Is.LessThan(before).And.GreaterThan(0));
    }

    [SetCulture("en-US")]
    [TestCase(null, "1.0 GB of 31.0 GB  ·  measuring...  ·  10/4287 files")]
    [TestCase(104857600.0, "1.0 GB of 31.0 GB  ·  100.0 MB/s  ·  10/4287 files")]
    public void FormatDetail_AlwaysShowsASpeed(double? bytesPerSecond, string expected)
    {
        // Arrange
        var progress = new SteamDownloadProgress(1024 * MEGABYTE, 31 * 1024 * MEGABYTE, 10, 4287, "Downloading");

        // Act
        var detail = SteamDownloadViewModel.FormatDetail(progress, bytesPerSecond);

        // Assert
        Assert.That(detail, Is.EqualTo(expected));
    }
}
