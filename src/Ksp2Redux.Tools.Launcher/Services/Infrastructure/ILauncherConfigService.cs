using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

/// <summary>
/// The configured installation identities, feed selection and controlled launcher storage.
/// </summary>
public interface ILauncherConfigService
{
    /// <summary>
    /// Gets the current launcher configuration.
    /// </summary>
    LauncherConfig Config { get; }

    /// <summary>
    /// Occurs after configuration persistence.
    /// </summary>
    event Action? ConfigSaved;

    /// <summary>
    /// Persist the current configuration.
    /// </summary>
    void Save();

    /// <summary>
    /// Persist the current configuration and report whether that worked.
    /// </summary>
    /// <remarks>
    /// For callers that must not go on when the data is not durable. A failure is reported like <see cref="Save"/> does, and returned.
    /// </remarks>
    /// <returns>True when the configuration was written.</returns>
    bool TrySave();

    /// <summary>
    /// Resolve the configured local launcher storage directory.
    /// </summary>
    /// <returns>The absolute storage path.</returns>
    string GetLocalStorageDirectory();
}
