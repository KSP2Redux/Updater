namespace Ksp2Redux.Tools.Launcher.ViewModels.Steam;

/// <summary>
/// Turns a running byte count into a download speed smoothed by an exponential moving average.
/// </summary>
internal sealed class TransferRateMeter
{
    private static readonly TimeSpan SAMPLE_INTERVAL = TimeSpan.FromMilliseconds(200);
    private const double SMOOTHING = 0.2;

    private TimeSpan _lastSampleTime;
    private long _lastSampleBytes;
    private bool _hasBaseline;

    /// <summary>The smoothed speed in bytes per second, or null until the first measurement.</summary>
    public double? BytesPerSecond { get; private set; }

    /// <summary>
    /// Records the total bytes transferred so far at <paramref name="elapsed"/> since the start.
    /// </summary>
    public void Sample(long totalBytes, TimeSpan elapsed)
    {
        if (!_hasBaseline)
        {
            _lastSampleTime = elapsed;
            _lastSampleBytes = totalBytes;
            _hasBaseline = true;
            return;
        }

        var interval = elapsed - _lastSampleTime;
        if (interval < SAMPLE_INTERVAL) return;

        var instant = (totalBytes - _lastSampleBytes) / interval.TotalSeconds;
        BytesPerSecond = BytesPerSecond is { } previous
            ? previous + SMOOTHING * (instant - previous)
            : instant;

        _lastSampleTime = elapsed;
        _lastSampleBytes = totalBytes;
    }
}
