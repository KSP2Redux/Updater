using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

// Not sure if this is needed, maybe LauncherConfig could be the service directly
// I'm just not sure how to handle LauncherConfig.GetOrCreateCurrentConfig()
public interface ILauncherConfigService
{
    LauncherConfig Config { get; }
}

public class LauncherConfigService(IFileSystem fileSystem) : ILauncherConfigService
{
    public LauncherConfig Config { get; set; } = LauncherConfig.GetOrCreateCurrentConfig(fileSystem);
}