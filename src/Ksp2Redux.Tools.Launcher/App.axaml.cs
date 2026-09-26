using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
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
            ShowFatalError("KSP2 Redux failed to start", ex);
            // IEnvironmentProvider isn't available - the container that would provide it is exactly
            // what just failed to build.
#pragma warning disable RS0030
            Environment.Exit(1);
#pragma warning restore RS0030
            return;
        }

        var log = _serviceProvider.GetRequiredService<ILogService>();
        log.Info($"Launcher starting. Log file: {log.CurrentLogFilePath ?? "(console only)"}");
        log.Info($"Launcher {typeof(App).Assembly.GetName().Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}.");
        if (Program.RenderingFlags.Length > 0) log.Info($"Rendering overridden with {Program.RenderingFlags}.");
        AvaloniaLogSink.Target = log;

        HookGlobalExceptionHandlers(log);

        LoadGlobalStylesheet(log);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>()
            };
            mainWindow.Opened += (_, _) => log.Info($"Main window opened with transparency level {mainWindow.ActualTransparencyLevel}.");
            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) =>
            {
                log.Info("Launcher exiting.");
                AvaloniaLogSink.Target = null;
                (log as IDisposable)?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void AboutMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (_serviceProvider is null) return;
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        await _serviceProvider.GetRequiredService<IMessageBoxService>().ShowMessageBoxAsOwnedAsync(
            "About KSP2 Redux",
            $"KSP2 Redux Launcher\nVersion {version}\n\n" +
            "The installer, updater and launcher for KSP2 Redux.\n" +
            "https://ksp2redux.org");
    }

    private async void CheckForUpdatesMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (_serviceProvider is null) return;
        try
        {
            await _serviceProvider.GetRequiredService<IUpdateService>().CheckAndPerformUpdateAsync();
        }
        catch (Exception ex)
        {
            _serviceProvider.GetService<ILogService>()?.Error("Manual update check from the app menu failed.", ex);
        }
    }

    // Deliberately bypasses Avalonia/MsBox.Avalonia entirely - this runs when the DI container or
    // Avalonia itself has already failed, so neither can be trusted to show a dialog.
    internal static void ShowFatalError(string headline, Exception ex)
    {
        var message = $"{headline}:\n\n{ex.Message}\n\n" +
                      $"More detail was written to {LogService.BootstrapLogPath}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                NativeMessageBox.ShowError(message, "KSP2 Redux");
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
