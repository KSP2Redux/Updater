using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Common;

namespace Ksp2Redux.Tools.Launcher.Models;

public class InstallPlan
{
    private const string EPIC_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.epic-prepatch.patch";
    private const string STEAM_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.steam-prepatch.patch";
    private const string PORTABLE_PREPATCH_NAME = "Ksp2Redux.Tools.Launcher.portable-prepatch.patch";

    private static Assembly _thisAssembly = Assembly.GetExecutingAssembly();


    public record struct Step(InstallPlanAction Action, string? Argument = null);

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
        Steps.Insert(0,new Step(InstallPlanAction.ApplyPatchFile, path));
    }
    
    public int Length => Steps.Count;

    public async Task ApplyToFolder(string install, Action<string> log)
    {
        foreach (var step in Steps)
        {
            switch (step.Action)
            {
                case InstallPlanAction.Uninstall:
                    log("Uninstalling KSP2 Redux");
                    Cache.RecursivelyRestoreCache(install);
                    break;
                case InstallPlanAction.RevertToStock:
                    log("Reverting KSP2 Redux to Stock for repatching");
                    Cache.RecursivelyRestoreCache(install, true);
                    break;
                case InstallPlanAction.Prepatch:
                {
                    log("Applying the correct prepatch");
                    if (!File.Exists(Path.Combine(install, "uninstall.zip")))
                    {
                        Cache.RecursivelyCreateCache(install);
                    }

                    var patchFile = Path.GetTempFileName();
                    var exe = Path.Combine("install", Ksp2Install.KSP2_EXE_NAME);
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

                    var patch = Ksp2Patch.FromFile(patchFile);
                    await patch.AsyncApply(install, install, log, log);
                    delete_patch:
                    File.Delete(patchFile);
                    break;
                }

                case InstallPlanAction.ApplyPatchFile:
                {
                    var patch = Ksp2Patch.FromFile(step.Argument!);
                    await patch.AsyncApply(install, install, log, log);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}