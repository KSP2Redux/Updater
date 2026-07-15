using System;
using System.IO.Abstractions;
using Ksp2Redux.Tools.Common.Service;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher;

public static class DefaultServiceProviderProvider
{
    public static IServiceProvider GetDefaultServiceProvider()
    {
        ServiceCollection serviceCollection = new();
        serviceCollection.AddSingleton<MainWindowViewModel>();
        serviceCollection.AddSingleton<HomeTabViewModel>();
        serviceCollection.AddSingleton<CommunityTabViewModel>();
        serviceCollection.AddSingleton<ModsTabViewModel>();
        serviceCollection.AddSingleton<SettingsTabViewModel>();
        serviceCollection.AddSingleton<IKsp2InstallService, Ksp2InstallService>();
        serviceCollection.AddSingleton<INewsItemCollectionService, NewsItemCollectionService>();
        serviceCollection.AddSingleton<ILogService, LogService>();
        serviceCollection.AddSingleton<ILauncherConfigService, LauncherConfigService>();
        serviceCollection.AddSingleton<IReleasesFeedService, ReleasesFeedService>();
        serviceCollection.AddSingleton<ITabNavigatorService, TabNavigatorService>();
        serviceCollection.AddSingleton<IFileSystem, RealFileSystem>();
        serviceCollection.AddSingleton<ICacheService, CacheService>();
        serviceCollection.AddSingleton<INewsService, NewsService>();
        serviceCollection.AddSingleton(SystemEnvironmentProvider.Instance);
        serviceCollection.AddSingleton<IAssemblyService, ExecutingAssemblyService>();
        serviceCollection.AddSingleton<IInstallPlanService, InstallPlanService>();
        serviceCollection.AddSingleton<IModuleDefinitionService, ModuleDefinitionService>();
        serviceCollection.AddSingleton<INewsProviderService, NewsProviderService>();
        serviceCollection.AddSingleton<IManifestReleasesFeedProviderService, ManifestReleasesFeedProviderService>();
        serviceCollection.AddSingleton<IZipFileService, ZipFileService>();
        serviceCollection.AddSingleton<IUpdateService, UpdateService>();
        serviceCollection.AddSingleton<IKsp2DetectorService, Ksp2DetectorService>();
        serviceCollection.AddSingleton<IMessageBoxService, MessageBoxService>();
        serviceCollection.AddSingleton<IOperatingSystemService, OperatingSystemService>();
        serviceCollection.AddSingleton<IDiskSpaceService, DiskSpaceService>();
        serviceCollection.AddSingleton<IWindowPlacementService, WindowPlacementService>();
        return serviceCollection.BuildServiceProvider();
    }
}