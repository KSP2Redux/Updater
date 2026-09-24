namespace Ksp2Redux.Tools.Launcher.ViewModels.Steam;

/// <summary>
/// Turns a running byte count into a steady download speed for display.
/// </summary>
// Chunks arrive in bursts, so the speed between two consecutive progress reports swings wildly.
// Samples are taken at most every SAMPLE_INTERVAL and folded into an exponential moving average,
// which never resets. So the number moves smoothly and does not drop to zero at a window boundary,
// and a real stall still shows as the average easing down towards zero.
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
