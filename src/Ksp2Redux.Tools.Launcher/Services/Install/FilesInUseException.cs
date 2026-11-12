namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Thrown by <see cref="InstallPlanService.ApplyToFolder"/> before anything changes, when other programs hold game
/// files open. Patching and bundle conversion both rewrite files in place, so going ahead would leave a mix of old
/// and new files.
/// </summary>
public sealed class FilesInUseException(string message, IReadOnlyList<string> files, IReadOnlyList<string> programs)
    : InvalidOperationException(message)
{
    /// <summary>The files held open, relative to the KSP2 folder.</summary>
    public IReadOnlyList<string> Files { get; } = files;

    /// <summary>The programs holding them, as far as the operating system can tell. Can be empty.</summary>
    public IReadOnlyList<string> Programs { get; } = programs;
}