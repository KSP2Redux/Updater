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
    /// Resolve the configured local launcher storage directory.
    /// </summary>
    /// <returns>The absolute storage path.</returns>
    string GetLocalStorageDirectory();
}
