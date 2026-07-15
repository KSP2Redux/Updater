using System;

namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

public interface IOperatingSystemService
{
    bool IsLinux();
}

public class OperatingSystemService : IOperatingSystemService
{
    public bool IsLinux()
#pragma warning disable RS0030
        => OperatingSystem.IsLinux();
#pragma warning restore RS0030
}