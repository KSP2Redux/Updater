using System;
using System.IO.Abstractions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
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

namespace Ksp2Redux.Tools.Launcher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // From Avalonia doc, because we're using Community Toolkit we don't want duplicate validation
        // https://docs.avaloniaui.net/docs/app-development/dependency-injection#step-1-install-the-nuget-package-for-di
        BindingPlugins.DataValidators.RemoveAt(0);
        
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
        
        ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        DataAnnotationsValidationPlugin[] dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (DataAnnotationsValidationPlugin plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}