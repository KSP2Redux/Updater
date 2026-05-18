using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Ksp2Redux.Tools.Launcher.Services;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Ksp2Redux.Tools.Launcher;

public partial class App(IServiceProvider? serviceProvider = null) : Application
{
    private IServiceProvider? _serviceProvider = serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _serviceProvider ??= DefaultServiceProviderProvider.GetDefaultServiceProvider();

        var log = _serviceProvider.GetRequiredService<ILogService>();
        log.Info($"Launcher starting. Log file: {log.CurrentLogFilePath ?? "(console only)"}");

        HookGlobalExceptionHandlers(log);

        LoadGlobalStylesheet(log);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.Exit += (_, _) =>
            {
                log.Info("Launcher exiting.");
                (log as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void HookGlobalExceptionHandlers(ILogService log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            log.Error($"Unhandled AppDomain exception (IsTerminating={args.IsTerminating}).", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    public static string NewsStylesheet { get; private set; } = string.Empty;
    private void LoadGlobalStylesheet(ILogService log)
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
            log.Error("Failed to load CSS for news rendering.", ex);
        }
    }
}