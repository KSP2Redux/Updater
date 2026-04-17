using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Launcher.Models;

public class InstallPlan
{
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

    public static InstallPlan operator +(InstallPlan a, InstallPlan b) => new() { Steps = a.Steps.Concat(b.Steps).ToList() };
}