using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

// Not sure if this is needed, maybe LauncherConfig could be the service directly
// I'm just not sure how to handle LauncherConfig.GetOrCreateCurrentConfig()
public interface ILauncherConfigService
{
    LauncherConfig Config { get; }
}

public class LauncherConfigService : ILauncherConfigService
{
    public LauncherConfig Config { get; set; }

    public LauncherConfigService()
    {
        Config = LauncherConfig.GetOrCreateCurrentConfig();
    }
}