using System.IO.Abstractions;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Microsoft.Extensions.DependencyInjection;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Factory for the service container the CLI commands run against.
/// </summary>
// This is the launcher's DefaultServiceProviderProvider minus everything that needs a window: the
// view models, news, tab navigation, window placement, and the launcher's own self update. What
// remains is exactly the set the launcher drives its install path with, so a CLI install exercises
// the same code as the button in the UI. The dialog service is the one substitution, because
// several of the remaining services raise dialogs on failure paths.
public static class CliServiceProvider
{
    /// <summary>
    /// Builds the container holding the launcher services the commands need.
    /// </summary>
    /// <param name="output">The writer the commands and the dialog stand in report through.</param>
    /// <returns>The service provider for a CLI session.</returns>
    public static IServiceProvider Build(CliOutput output)
    {
        ServiceCollection services = new();

        services.AddSingleton(output);
        services.AddSingleton<IMessageBoxService, CliMessageBoxService>();

        services.AddSingleton<IFileSystem, RealFileSystem>();
        services.AddSingleton(SystemEnvironmentProvider.Instance);

        // Registered by interface as well, because self-update needs the running executable's path
        // and self-uninstall needs to edit the user's PATH.
        services.AddSingleton<IEnvironmentProvider>(SystemEnvironmentProvider.Instance);
        services.AddSingleton<IEnvironmentVariableProvider>(SystemEnvironmentProvider.Instance);
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<ILauncherConfigService, LauncherConfigService>();
        services.AddSingleton<IAssemblyService, ExecutingAssemblyService>();
        services.AddSingleton<IModuleDefinitionService, ModuleDefinitionService>();
        services.AddSingleton<IOperatingSystemService, OperatingSystemService>();
        services.AddSingleton<IZipFileService, ZipFileService>();
        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IDiskSpaceService, DiskSpaceService>();
        services.AddSingleton<IKsp2DetectorService, Ksp2DetectorService>();
        services.AddSingleton<IKsp2InstallService, Ksp2InstallService>();
        services.AddSingleton<IGameDataFolderService, GameDataFolderService>();
        services.AddSingleton<IManifestReleasesFeedProviderService, ManifestReleasesFeedProviderService>();
        services.AddSingleton<IReleasesFeedService, ReleasesFeedService>();
        services.AddSingleton<IInstallPlanService, InstallPlanService>();

        return services.BuildServiceProvider();
    }
}
