using System.Diagnostics;
using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Mac;

namespace Ksp2Redux.Tools.Launcher.Services.Game;

/// <summary>
/// Existing game lifecycle and native process detection used by GUI, CLI and installation guards.
/// </summary>
/// <param name="operatingSystem">The host platform.</param>
/// <param name="wineRuntime">The legacy Wine adapter.</param>
/// <param name="log">The launcher log.</param>
/// <param name="fileSystem">The host path abstraction.</param>
public class GameProcessService(IOperatingSystemService operatingSystem, IWineRuntimeService wineRuntime, ILogService log,
    IFileSystem fileSystem) : IGameProcessService
{
    private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public void Start(ProcessStartInfo startInfo)
    {
        using var process = StartTracked(startInfo);
    }

    /// <inheritdoc />
    public Process? StartTracked(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        if (process is null && !startInfo.UseShellExecute)
        {
            throw LocalizationService.Default.Message("Game.DidNotStart", startInfo.FileName)
                .ToException(message => new InvalidOperationException(message));
        }
        return process;
    }

    public bool IsRunning() => FindGame().Count > 0;

    /// <inheritdoc />
    public bool IsRunning(string installRoot)
    {
        if (!operatingSystem.IsWindows())
            return IsRunning();
        string intendedExe = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(installRoot, Ksp2Install.KSP2_EXE_NAME));
        foreach (int pid in FindGame(requireReliableInspection: true))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                string? executable = process.MainModule?.FileName;
                if (executable is null || fileSystem.Path.GetFullPath(executable).Equals(intendedExe, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (ArgumentException)
            {
                // The process exited between discovery and inspection.
            }
            catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                log.Warn($"Could not identify running KSP2 process {pid}: {failure.Message}");
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> WaitForStartAsync(string installRoot, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!IsRunning(installRoot))
        {
            if (waited.Elapsed >= timeout)
                return false;
            await Task.Delay(StartPollInterval, cancellationToken);
        }
        return true;
    }

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

        List<LogMessage> failures = [];
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
                failures.Add(LocalizationService.Default.Message("Game.StopFailure", pid, LogMessage.Literal(e.Message)));
            }
        }

        if (failures.Count > 0)
        {
            var joined = new LogMessage(
                string.Join(", ", failures.Select(failure => failure.English)),
                string.Join(", ", failures.Select(failure => failure.Localized)));
            throw LocalizationService.Default.Message("Game.CouldNotStop", joined)
                .ToException(message => new InvalidOperationException(message));
        }
    }

    private IReadOnlyList<int> FindGame(bool requireReliableInspection = false)
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
                if (requireReliableInspection)
                    throw new IOException($"Could not inspect KSP2 process use before installation: {e.Message}", e);
                log.Warn($"Could not look for {name}: {e.Message}");
            }
        }
        return found;
    }
}
