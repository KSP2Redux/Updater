using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Common;

namespace Ksp2Redux.Tools.Launcher.Models;

public class InstallPlan
{
    private const string EPIC_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.epic-prepatch.patch";
    private const string STEAM_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.steam-prepatch.patch";
    private const string PORTABLE_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.Prepatches.portable-prepatch.patch";

    private static Assembly _thisAssembly = Assembly.GetExecutingAssembly();


    // The Argument is string getPath(log, progress)
    public record struct Step(InstallPlanAction Action, Func<Action<string>, Action<long, long>, CancellationToken, Task<string>>? Argument = null, string ArgumentDescription = "Undescribed");

    public List<Step> Steps = [];

    public void Uninstall()
    {
        Steps.Insert(0,new Step(InstallPlanAction.Uninstall));
    }

    public void RevertToStock()
    {
        Steps.Insert(0,new Step(InstallPlanAction.RevertToStock));
    }

    public void Prepatch()
    {
        Steps.Insert(0,new Step(InstallPlanAction.Prepatch));
    }

    public void ApplyPatchFile(string path)
    {
        Steps.Insert(0,new Step(InstallPlanAction.ApplyPatchFile, (_,_,_) => Task.FromResult(path),$"Applying patch file at {path}"));
    }

    public void ApplyPatchFile(Func<Action<string>, Action<long, long>,CancellationToken,Task<string>> path, string description)
    {
        Steps.Insert(0, new Step(InstallPlanAction.ApplyPatchFile, path, description));
    }

    // We treat reverting to stock as 1000 cost cuz we should really try to avoid it, but we still want to do the shortest
    // path that involves it if necessary
    public int Cost => Steps.Count + (Steps.Any(x => x.Action == InstallPlanAction.RevertToStock) ? 1000 : 0);

    public static InstallPlan operator +(InstallPlan a, InstallPlan b) => new()
    {
        Steps = a.Steps.Concat(b.Steps).ToList() 
    };

    public void Describe(Action<string> log)
    {
        log($"Install/Update plan has been made, it has {Steps.Count} steps.");
        for (var i = 0; i < Steps.Count; i++)
        {
            switch (Steps[i].Action)
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
                    log($"Step {i + 1}: {Steps[i].ArgumentDescription}");
                    break;
                case InstallPlanAction.CopyFrom:
                    log($"Step {i + 1}: (This should be impossible rn)");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    public async Task ApplyToFolder(string install, Action<string> log, Action<long, long> downloadProgress, Action<int, int> stepsProgress, CancellationToken ct)
    {
        var i = 0;
        foreach (var step in Steps)
        {
            stepsProgress(++i, Steps.Count);
            switch (step.Action)
            {
                case InstallPlanAction.Uninstall:
                    log("Uninstalling KSP2 Redux");
                    Cache.RecursivelyRestoreCache(install);
                    break;
                case InstallPlanAction.RevertToStock:
                    if (File.Exists(Path.Combine(install, "uninstall.zip")))
                    {
                        log("Reverting KSP2 Redux to Stock for repatching");
                        Cache.RecursivelyRestoreCache(install, true);
                    }
                    else
                    {
                        log("Did not need to revert to stock");
                    }
                    break;
                case InstallPlanAction.Prepatch:
                {
                    log("Applying the correct prepatch");
                    if (!File.Exists(Path.Combine(install, "uninstall.zip")))
                    {
                        Cache.RecursivelyCreateCache(install);
                    }

                    var patchFile = Path.GetTempFileName();
                    var exe = Path.Combine(install, Ksp2Install.KSP2_EXE_NAME);
                    var ksp2Install = new Ksp2Install(exe);
                    switch (ksp2Install.Distribution)
                    {
                        case Distribution.Portable:
                        {
                            log("Applying portable prepatch");
                            await using var stream = _thisAssembly.GetManifestResourceStream(PORTABLE_PREPATCH_NAME);
                            await using var fstream =
                                new FileStream(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Steam:
                        {
                            log("Applying steam prepatch");
                            await using var stream = _thisAssembly.GetManifestResourceStream(STEAM_PREPATCH_NAME);
                            await using var fstream =
                                new FileStream(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                            await stream?.CopyToAsync(fstream)!;
                            await fstream.FlushAsync();
                            break;
                        }
                        case Distribution.Epic:
                        {
                            log("Applying epic prepatch");
                            await using var stream = _thisAssembly.GetManifestResourceStream(EPIC_PREPATCH_NAME);
                            await using var fstream =
                                new FileStream(patchFile, FileMode.OpenOrCreate, FileAccess.ReadWrite);
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

                    using (var patch = Ksp2Patch.FromFile(patchFile))
                    {
                        await patch.AsyncApply(install, install, log, log);
                    }
                    
                    delete_patch:
                    File.Delete(patchFile);
                    break;
                }

                case InstallPlanAction.ApplyPatchFile:
                {
                    var patch = Ksp2Patch.FromFile(await step.Argument!(log, downloadProgress, ct));
                    await patch.AsyncApply(install, install, log, log);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            await Task.Delay(250, ct);
        }
    }
}