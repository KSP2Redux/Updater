namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Thrown by <see cref="InstallPlanService.ApplyToFolder"/> when a patch-apply step fails partway
/// through. <see cref="RolledBack"/> indicates whether the install directory was successfully restored
/// to its last known-good state (from uninstall.zip) before this was thrown.
/// </summary>
public sealed class InstallFailedException(string message, Exception innerException, bool rolledBack)
    : Exception(message, innerException)
{
    public bool RolledBack { get; } = rolledBack;
}
