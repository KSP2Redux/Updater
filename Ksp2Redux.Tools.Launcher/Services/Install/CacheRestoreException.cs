using System;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Thrown by <see cref="CacheService.RecursivelyRestoreCache"/> when the pre-patch snapshot it needs
/// to restore from is missing, carrying the directory and expected file so the failure is
/// distinguishable from other exceptions in a log without inspecting the message text.
/// </summary>
public sealed class CacheRestoreException(string directory, string expectedFile)
    : Exception($"Original stock files were deleted - expected \"{expectedFile}\" under \"{directory}\", uninstallation is impossible.")
{
    public string Directory { get; } = directory;
    public string ExpectedFile { get; } = expectedFile;
}
