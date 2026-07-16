using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public interface IInstallPlanService
{
    void Describe(InstallPlan installPlan, Action<string> log);

    Task ApplyToFolder(InstallPlan installPlan, string install, Action<string> log,
        Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct);
}

public class InstallPlanService(IFileSystem fileSystem, ICacheService cacheService, IEnvironmentProvider environmentProvider,
    IAssemblyService assemblyService, IModuleDefinitionService moduleDefinitionService, IZipFileService zipFileService,
    IDiskSpaceService diskSpaceService) : IInstallPlanService
{
    private const string EPIC_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.epic-prepatch.patch";
    private const string STEAM_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.steam-prepatch.patch";
    private const string PORTABLE_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.portable-prepatch.patch";

    // Extra headroom on top of the raw estimate below, since patch application briefly holds both the
    // old and new copy of a changed file, and the download itself needs to sit on disk before it's applied.
    private const double RequiredSpaceSafetyMargin = 1.2;
    
    public void Describe(InstallPlan installPlan, Action<string> log)
    {
        log($"Install/Update plan has been made, it has {installPlan.Steps.Count} steps.");
        for (var i = 0; i < installPlan.Steps.Count; i++)
        {
            switch (installPlan.Steps[i].Action)
            {
                case InstallPlanAction.Uninstall:
                    log($"Step {i + 1}: Uninstalling KSP2 Redux");
                    break;
                case InstallPlanAction.RevertToStock:
                    log($"Step {i + 1}: Revert to Stock Install");
                    break;
                case InstallPlanAction.Prepatch:
                    log($"Step {i + 1}: Going to prepatch from stock");
                    break;
                case InstallPlanAction.ApplyPatchFile:
                    log($"Step {i + 1}: {installPlan.Steps[i].ArgumentDescription}");
                    break;
                case InstallPlanAction.CopyFrom:
                    log($"Step {i + 1}: (This should be impossible rn)");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public async Task ApplyToFolder(InstallPlan installPlan, string install, Action<string> log,
        Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct)
    {
        EnsureEnoughDiskSpace(installPlan, install, log);

        var i = 0;
        foreach (var step in installPlan.Steps)
        {
            switch (step.Action)
            {
                case InstallPlanAction.Uninstall:
                    log("Uninstalling KSP2 Redux");
                    cacheService.RecursivelyRestoreCache(install);
                    break;
                case InstallPlanAction.RevertToStock:
                    if (fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
                    {
                        log("Reverting KSP2 Redux to Stock for repatching");
                        cacheService.RecursivelyRestoreCache(install, true);
                    }
                    else
                    {
                        log("Did not need to revert to stock");
                    }
                    break;
                case InstallPlanAction.Prepatch:
                {
                    log("Applying the correct prepatch");
                    if (fileSystem.File.Exists(fileSystem.Path.Combine(install, "winhttp.dll")))
                    {
                        log("Deleting old modloader!");
                        fileSystem.File.Delete(fileSystem.Path.Combine(install, "winhttp.dll"));
                    }
                    
                    if (!fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
                    {
                        cacheService.RecursivelyCreateCache(install);
                    }

                    var patchFile = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), fileSystem.Path.GetRandomFileName());
                    var exe = fileSystem.Path.Combine(install, Ksp2Install.KSP2_EXE_NAME);
                    var ksp2Install = new Ksp2Install(fileSystem, moduleDefinitionService, exe);
                    switch (ksp2Install.Distribution)
                    {
                        case Distribution.Portable:
                        {
                            log("Applying portable prepatch");
                            await using var stream = assemblyService.GetManifestResourceStream(PORTABLE_PREPATCH_NAME);
                            await using var fstream =
                                fileSystem.FileStream.New(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Steam:
                        {
                            log("Applying steam prepatch");
                            await using var stream = assemblyService.GetManifestResourceStream(STEAM_PREPATCH_NAME);
                            await using var fstream =
                                fileSystem.FileStream.New(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Epic:
                        {
                            log("Applying epic prepatch");
                            await using var stream = assemblyService.GetManifestResourceStream(EPIC_PREPATCH_NAME);
                            await using var fstream =
                                fileSystem.FileStream.New(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Redux:
                            throw new Exception("Cannot apply prepatch to Redux install");
                        case Distribution.Prepatched:
                            // Shouldn't happen, but *could*
                            goto delete_patch;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    try
                    {
                        using var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchFile);   // Test: Convert to factory, add interface for patch
                        await patch.AsyncApply(environmentProvider, install, install, log, log);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RollbackOrRethrow(install, log, ex);
                    }

                    delete_patch:
                    fileSystem.File.Delete(patchFile);
                    break;
                }

                case InstallPlanAction.ApplyPatchFile:
                {
                    var patchPath = await step.Argument!(log, downloadProgress, ct);
                    try
                    {
                        using var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchPath);
                        await patch.AsyncApply(environmentProvider, install, install, log, log);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RollbackOrRethrow(install, log, ex);
                    }
                    if (step.DeleteAfter && fileSystem.File.Exists(patchPath))
                    {
                        log($"Cleaning up downloaded patch {patchPath}");
                        try { fileSystem.File.Delete(patchPath); }
                        catch (Exception ex) { log($"Failed to delete patch {patchPath}: {ex.Message}"); }
                    }
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
            stepsProgress(++i, installPlan.Steps.Count);
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Called when a Prepatch/ApplyPatchFile step throws partway through. If a known-good snapshot
    /// (uninstall.zip) exists, restores it so a corrupt patch or a mid-write failure (e.g. disk full)
    /// doesn't leave the install half-patched, then throws <see cref="InstallFailedException"/> describing
    /// what happened. Note this restores the whole install to the last snapshot, which may discard other
    /// patches already applied earlier in the same plan - still strictly better than a half-patched install.
    /// If there is no snapshot to restore (nothing has been written to the install dir yet), rethrows the
    /// original exception unchanged.
    /// </summary>
    private void RollbackOrRethrow(string install, Action<string> log, Exception original)
    {
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip")))
        {
            ExceptionDispatchInfo.Capture(original).Throw();
            return; // unreachable, keeps the compiler happy
        }

        log($"Install step failed ({original.Message}). Rolling back to the last known-good state...");

        Exception? rollbackFailure = null;
        try
        {
            cacheService.RecursivelyRestoreCache(install, true);
        }
        catch (Exception rollbackEx)
        {
            rollbackFailure = rollbackEx;
        }

        if (rollbackFailure is null)
        {
            log("Rolled back successfully.");
            throw new InstallFailedException(
                $"Installation failed and was automatically rolled back to the previous state. Original error: {original.Message}",
                original, rolledBack: true);
        }

        log($"Rollback also failed: {rollbackFailure.Message}");
        throw new InstallFailedException(
            $"Installation failed ({original.Message}) and the automatic rollback also failed ({rollbackFailure.Message}). " +
            "The install may be in a broken state - try Uninstall or Revert to Stock from Settings.",
            original, rolledBack: false);
    }

    private void EnsureEnoughDiskSpace(InstallPlan installPlan, string install, Action<string> log)
    {
        long requiredBytes = installPlan.Cost;

        // The first prepatch on an install snapshots the whole directory into uninstall.zip alongside it,
        // so that needs to fit on the same drive too.
        bool needsCacheSnapshot = installPlan.Steps.Any(s => s.Action == InstallPlanAction.Prepatch) &&
                                   !fileSystem.File.Exists(fileSystem.Path.Combine(install, "uninstall.zip"));
        if (needsCacheSnapshot)
        {
            requiredBytes += GetDirectorySize(install);
        }

        requiredBytes = (long)(requiredBytes * RequiredSpaceSafetyMargin);
        if (requiredBytes <= 0) return;

        var availableBytes = diskSpaceService.GetAvailableFreeSpace(install);
        if (availableBytes is null)
        {
            log("Could not determine available disk space, skipping pre-flight space check.");
            return;
        }

        if (availableBytes < requiredBytes)
        {
            throw new InvalidOperationException(
                $"Not enough free disk space to continue: need approximately {FormatBytes(requiredBytes)}, " +
                $"but only {FormatBytes(availableBytes.Value)} is available. Free up some space and try again.");
        }
    }

    private long GetDirectorySize(string directory)
    {
        long total = 0;
        foreach (var file in fileSystem.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += fileSystem.FileInfo.New(file).Length; }
            catch (IOException) { /* file may have been removed/locked concurrently; ignore for this estimate */ }
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F0} MB";
    }
}