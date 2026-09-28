using System.Diagnostics;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Mac;

namespace Ksp2Redux.Tools.Launcher.Services.Game;

public interface IGameProcessService
{
    /// <summary>
    /// Starts a launch: the game itself, Steam's rungameid URL or the Wine runtime.
    /// </summary>
    void Start(ProcessStartInfo startInfo);

    /// <summary>
    /// True while a KSP2 process is running, however it was started.
    /// </summary>
    bool IsRunning();

    /// <summary>
    /// Waits for KSP2 to show up after a launch.
    /// </summary>
    /// <returns>False when it did not appear within <paramref name="timeout"/>.</returns>
    Task<bool> WaitForStartAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until no KSP2 process is left running.
    /// </summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops every running KSP2 process. Anything unsaved in the game is lost.
    /// </summary>
    Task StopAsync();
}

public class GameProcessService(IOperatingSystemService operatingSystem, IWineRuntimeService wineRuntime, ILogService log) : IGameProcessService
{
    private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    public void Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process is null && !startInfo.UseShellExecute)
        {
            throw new InvalidOperationException($"{startInfo.FileName} did not start.");
        }
    }

    public bool IsRunning() => FindGame().Count > 0;

    public async Task<bool> WaitForStartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!IsRunning())
        {
            if (waited.Elapsed >= timeout) return false;
            await Task.Delay(StartPollInterval, cancellationToken);
        }
        return true;
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (IsRunning())
        {
            await Task.Delay(ExitPollInterval, cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        if (operatingSystem.IsMacOS() && wineRuntime.Detect() is { } runtime)
        {
            try
            {
                using var stop = Process.Start(wineRuntime.CreateStopInfo(runtime));
                if (stop is not null) await stop.WaitForExitAsync().WaitAsync(KillTimeout);
            }
            catch (Exception e)
            {
                log.Warn($"Could not end the Wine session: {e.Message}");
            }
        }

        List<string> failures = [];
        foreach (var pid in FindGame())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(KillTimeout);
            }
            catch (ArgumentException)
            {
            }
            catch (Exception e)
            {
                failures.Add($"pid {pid}: {e.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"Could not stop KSP2 ({string.Join(", ", failures)}).");
        }
    }

    private IReadOnlyList<int> FindGame()
    {
        if (operatingSystem.IsMacOS()) return GameProcessFinder.FindUnderWine();

        var exeName = Ksp2Install.KSP2_EXE_NAME;
        var processName = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
        List<int> found = [];
        foreach (var name in new[] { processName, exeName })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    found.Add(process.Id);
                    process.Dispose();
                }
            }
            catch (Exception e)
            {
                log.Warn($"Could not look for {name}: {e.Message}");
            }
        }
        return found;
    }
}
