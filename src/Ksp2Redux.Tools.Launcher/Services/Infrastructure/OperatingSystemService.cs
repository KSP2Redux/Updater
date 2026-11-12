namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

public interface IOperatingSystemService
{
    bool IsLinux();

    bool IsMacOS();

    bool IsWindows();
}

public class OperatingSystemService : IOperatingSystemService
{
#pragma warning disable RS0030
    public bool IsLinux() => OperatingSystem.IsLinux();

    public bool IsMacOS() => OperatingSystem.IsMacOS();

    public bool IsWindows() => OperatingSystem.IsWindows();
#pragma warning restore RS0030
}
