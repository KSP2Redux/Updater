using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Common.Service;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IInstallPlanService
{
    void Describe(InstallPlan installPlan, Action<string> log);

    Task ApplyToFolder(InstallPlan installPlan, string install, Action<string> log,
        Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct);
}

public class InstallPlanService(IFileSystem fileSystem, ICacheService cacheService, IEnvironmentProvider environmentProvider,
    IAssemblyService assemblyService, IModuleDefinitionService moduleDefinitionService, IZipFileService zipFileService) : IInstallPlanService
{
    private const string EPIC_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.epic-prepatch.patch";
    private const string STEAM_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.steam-prepatch.patch";
    private const string PORTABLE_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.portable-prepatch.patch";
    
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

                    var patchFile = fileSystem.Path.GetTempFileName();
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

                    using (var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchFile))   // Test: Convert to factory, add interface for patch
                    {
                        await patch.AsyncApply(environmentProvider, install, install, log, log);
                    }
                    
                    delete_patch:
                    fileSystem.File.Delete(patchFile);
                    break;
                }

                case InstallPlanAction.ApplyPatchFile:
                {
                    var patchPath = await step.Argument!(log, downloadProgress, ct);
                    using (var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchPath))
                    {
                        await patch.AsyncApply(environmentProvider, install, install, log, log);
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
}