using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

/// <summary>Optional cumulative recipe phase timings in seconds, including nested phases.</summary>
public static class RecipePerformance
{
    private static readonly ConcurrentDictionary<string, long> Ticks = new();
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("KSP2REDUX_RECIPE_PROFILE") == "1";
    private static int _sourceHandlePeak;
    private static int _packHandlePeak;

    /// <summary>Gets the maximum cached native source handles observed in a profiled process.</summary>
    public static int SourceHandlePeak => _sourceHandlePeak;

    /// <summary>Gets the maximum cached pack handles observed in a profiled process.</summary>
    public static int PackHandlePeak => _packHandlePeak;

    /// <summary>Record a native source pool's open handle count.</summary>
    /// <param name="count">The current count.</param>
    public static void ObserveSourceHandles(int count)
        => ObserveMaximum(ref _sourceHandlePeak, count);

    /// <summary>Record a literal pack pool's open handle count.</summary>
    /// <param name="count">The current count.</param>
    public static void ObservePackHandles(int count)
        => ObserveMaximum(ref _packHandlePeak, count);

    private static void ObserveMaximum(ref int maximum, int count)
    {
        if (!Enabled)
            return;
        int previous;
        do
        {
            previous = maximum;
            if (previous >= count)
                return;
        } while (Interlocked.CompareExchange(ref maximum, count, previous) != previous);
    }

    /// <summary>Measure a named phase when recipe profiling is enabled.</summary>
    /// <param name="phase">The phase name.</param>
    /// <returns>The timing scope.</returns>
    public static Scope Measure(string phase) => new(phase);

    /// <summary>Read cumulative timings for this process.</summary>
    /// <returns>Named elapsed seconds. Nested timings overlap.</returns>
    public static Dictionary<string, double> Snapshot() => Ticks.ToDictionary(pair => pair.Key, pair => (double)pair.Value / Stopwatch.Frequency);

    /// <summary>A phase timer whose disposal records elapsed time.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string _phase;
        private readonly long _start;
        internal Scope(string phase)
        {
            _phase = phase;
            _start = Enabled ? Stopwatch.GetTimestamp() : 0;
        }
        /// <summary>Record this phase's elapsed time.</summary>
        public void Dispose()
        {
            if (_start != 0)
            {
                long elapsed = Stopwatch.GetTimestamp() - _start;
                Ticks.AddOrUpdate(_phase, static (_, duration) => duration,
                    static (_, ticks, duration) => ticks + duration, elapsed);
            }
        }
    }
}
