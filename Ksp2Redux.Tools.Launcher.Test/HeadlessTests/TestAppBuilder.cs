using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Ksp2Redux.Tools.Common.Service;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test.HeadlessTests;

public static class TestAppBuilder
{
    public static MockFileSystem FileSystem { get; private set; } = null!;
    public static Mock<IZipFileService> ZipFileService { get; private set; } = null!;
    public static Mock<IManifestReleasesFeedProviderService> ManifestReleasesFeedProviderService { get; private set; } = null!;
    public static MockEnvironmentProvider EnvironmentProvider { get; private set; } = null!;
    public static Mock<IModuleDefinitionService> ModuleDefinitionService { get; private set; } = null!;
    public static Mock<INewsProviderService> NewsProviderService { get; private set; } = null!;
    public static Mock<IAssemblyService> AssemblyService { get; private set; } = null!;
    public static Mock<IUpdateService> UpdateService { get; private set; } = null!;
    public static Mock<IMessageBoxService> MessageBoxService { get; private set; } = null!;
    public static Mock<IOperatingSystemService> OperatingSystemService { get; private set; } = null!;

    public static IServiceProvider ServiceProvider { get; set; } = null!;

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure(() =>
            {
                InitServiceProvider();  // Resets all services
                return new App(ServiceProvider);
            })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseSkia()
            .UseHeadless(new() { UseHeadlessDrawing = false});
    }
    
    private static void InitServiceProvider()
    {
        FileSystem = new();
        ZipFileService = new();
        ManifestReleasesFeedProviderService = new();
        EnvironmentProvider = new();
        ModuleDefinitionService = new();
        NewsProviderService = new();
        AssemblyService = new();
        MessageBoxService = new();
        OperatingSystemService = new();
        // Fully mocked for now, but could be tested if Http, Process and RuntimeInfo are separated in separated mockable interfaces
        UpdateService = new();

        ServiceCollection serviceCollection = new();
        serviceCollection.AddSingleton<MainWindowViewModel>();
        serviceCollection.AddSingleton<HomeTabViewModel>();
        serviceCollection.AddSingleton<CommunityTabViewModel>();
        serviceCollection.AddSingleton<ModsTabViewModel>();
        serviceCollection.AddSingleton<SettingsTabViewModel>();
        serviceCollection.AddSingleton<IKsp2InstallService, Ksp2InstallService>();
        serviceCollection.AddSingleton<INewsItemCollectionService, NewsItemCollectionService>();
        serviceCollection.AddSingleton<ILauncherConfigService, LauncherConfigService>();
        serviceCollection.AddSingleton<IReleasesFeedService, ReleasesFeedService>();
        serviceCollection.AddSingleton<ITabNavigatorService, TabNavigatorService>();
        serviceCollection.AddSingleton<IFileSystem>(FileSystem);
        serviceCollection.AddSingleton<ICacheService, CacheService>();
        serviceCollection.AddSingleton<INewsService, NewsService>();
        serviceCollection.AddSingleton<IEnvironmentProvider>(EnvironmentProvider);
        serviceCollection.AddSingleton(AssemblyService.Object);
        serviceCollection.AddSingleton<IInstallPlanService, InstallPlanService>();
        serviceCollection.AddSingleton(ModuleDefinitionService.Object);
        serviceCollection.AddSingleton(NewsProviderService.Object);
        serviceCollection.AddSingleton(ManifestReleasesFeedProviderService.Object);
        serviceCollection.AddSingleton(ZipFileService.Object);
        serviceCollection.AddSingleton(UpdateService.Object);
        serviceCollection.AddSingleton<IKsp2DetectorService, Ksp2DetectorService>(); 
        serviceCollection.AddSingleton(MessageBoxService.Object); 
        serviceCollection.AddSingleton(OperatingSystemService.Object); 
        ServiceProvider = serviceCollection.BuildServiceProvider();
    }
}