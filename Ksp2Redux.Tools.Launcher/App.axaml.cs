using System;
using System.IO;
using System.Runtime.InteropServices;
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
        // Building the DI container touches disk (config load), so it can fail before the logger or
        // any exception handler exists. Without this, a broken first run just silently vanishes with
        // nothing to go on - "launcher won't open" bug reports with zero log evidence.
        try
        {
            _serviceProvider ??= DefaultServiceProviderProvider.GetDefaultServiceProvider();
        }
        catch (Exception ex)
        {
            LogService.WriteEarly($"Fatal startup failure while initializing services: {ex}");
            ShowFatalStartupError(ex);
            // IEnvironmentProvider isn't available - the container that would provide it is exactly
            // what just failed to build.
#pragma warning disable RS0030
            Environment.Exit(1);
#pragma warning restore RS0030
            return;
        }

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

    // Deliberately bypasses Avalonia/MsBox.Avalonia entirely - the framework may not be usable yet at
    // this point, since this only runs when building the DI container (and therefore most of the app's
    // own services) already failed. A plain native message box has no dependency on any of that.
    private static void ShowFatalStartupError(Exception ex)
    {
        var message = $"KSP2 Redux failed to start:\n\n{ex.Message}\n\n" +
                      "A bootstrap.log with more detail was written to your logs folder.";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                NativeMessageBox.ShowError(message, "KSP2 Redux - Startup Failed");
                return;
            }
            catch (Exception showEx)
            {
                LogService.WriteEarly($"Could not show native startup-error dialog: {showEx}");
            }
        }
        Console.Error.WriteLine(message);
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