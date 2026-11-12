using System.Diagnostics;

namespace Ksp2Redux.Tools.Launcher.Services.Game;

/// <summary>
/// Existing game-process lifecycle and launch handoff operations.
/// </summary>
public interface IGameProcessService
{
    /// <summary>
    /// Start the configured game or compatibility-runtime handoff.
    /// </summary>
    /// <param name="startInfo">The executable or shell handoff.</param>
    void Start(ProcessStartInfo startInfo);

    /// <summary>
    /// Start a launch and retain its native process handle.
    /// </summary>
    /// <param name="startInfo">The executable or shell handoff.</param>
    /// <returns>The process handle, or null for an untracked shell handoff.</returns>
    Process? StartTracked(ProcessStartInfo startInfo);

    /// <summary>
    /// Check for a running game process.
    /// </summary>
    /// <returns>True if a game is running, false otherwise.</returns>
    bool IsRunning();

    /// <summary>
    /// Check process use of the intended installation.
    /// </summary>
    /// <remarks>
    /// Windows blocks inaccessible process identities. Legacy POSIX detection remains global.
    /// </remarks>
    /// <param name="installRoot">The intended installation directory.</param>
    /// <returns>True if the game is running or its identity is inaccessible, false otherwise.</returns>
    bool IsRunning(string installRoot);

    /// <summary>
    /// Wait for the intended installation after a launch handoff.
    /// </summary>
    /// <param name="installRoot">The intended installation directory.</param>
    /// <param name="timeout">The existing launch timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the game appears, false otherwise.</returns>
    Task<bool> WaitForStartAsync(string installRoot, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Wait for a game process after launch.
    /// </summary>
    /// <param name="timeout">The launch timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the game appears, false otherwise.</returns>
    Task<bool> WaitForStartAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Wait until game processes exit.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The wait task.</returns>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stop running game processes through the existing host route.
    /// </summary>
    /// <returns>The stop task.</returns>
    Task StopAsync();
}
