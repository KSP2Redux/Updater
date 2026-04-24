using System;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Common.Service;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.ViewModels.Mods;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ksp2Redux.Tools.Launcher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LoadGlobalStylesheet();

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
        serviceCollection.AddSingleton<IFileSystem, FileSystem>();
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
        
        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
    
    public static string NewsStylesheet { get; private set; } = string.Empty;
    private void LoadGlobalStylesheet()
    {
        try 
        {
            var uri = new Uri("avares://Ksp2Redux.Tools.Launcher/Assets/news.css");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            NewsStylesheet = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load CSS: {ex.Message}");
        }
    }
}