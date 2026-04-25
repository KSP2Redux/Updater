using System;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.ViewModels.Home;

public class Ksp2InstallChoiceViewModel(Ksp2InstallEntry entry)
{
    public Guid Id { get; } = entry.Id;
    public string Name { get; } = entry.Name;
    public string ExePath { get; } = entry.ExePath;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? ExePath : Name;
}
