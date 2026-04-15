using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IKsp2InstallService
{
    Ksp2Install? Ksp2 { get; }
    void TryLoadKsp2Install();
}

public class Ksp2InstallService(ILauncherConfigService launcherConfigService, IFileSystem fileSystem) : IKsp2InstallService
{
    public Ksp2Install? Ksp2 { get; private set; }
    
    public void TryLoadKsp2Install()
    {
        if (!string.IsNullOrWhiteSpace(launcherConfigService.Config.Ksp2InstallPath))
        {
            Ksp2 = new(fileSystem, launcherConfigService.Config.Ksp2InstallPath);
        }
    }
}