using Avalonia;
using Avalonia.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommandLine;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher;

sealed class Program
{
    public class Options
    {
        [Option("pid", Required = false, HelpText = "The process ID of the parent to kill if this is being rerun once updated")]
        public int? ParentProcessId { get; set; }

        [Option("exe", Required = false, HelpText = "The path of the executable to overwrite with this executable")]
        public string? Executable { get; set; }

        [Option("prev", Required = false, HelpText = "The previous executable path (used for cleanup in the 3 step update process")]
        public string? PreviousExecutablePath { get; set; }

        [Option("software-rendering", Required = false, HelpText = "Draw the launcher on the CPU instead of the GPU")]
        public bool SoftwareRendering { get; set; }

        [Option("no-composition", Required = false, HelpText = "Keep the GPU but skip the Windows UI composition layer")]
        public bool NoComposition { get; set; }
    }

    public static bool PartialUpdate { get; private set; }

    /// <summary>
    /// The rendering flags this launcher was started with, passed on to the new executable during a self-update.
    /// </summary>
    public static string RenderingFlags => GetRenderingFlags(_softwareRendering, _noComposition);

    private static bool _softwareRendering;
    private static bool _noComposition;

    private const int StageSettleDelay = 1000;

    private static void TryKillParent(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Kill(false);
            process.WaitForExit();
        }
        catch
        {
            // Failing to kill the parent (already exited, access denied, etc.) is expected
            // and explicitly not treated as a partial-update condition.
        }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.IsTerminating && e.ExceptionObject is Exception ex) ReportCrash(ex);
        };

        Parser.Default.ParseArguments<Options>(args).WithParsed(options =>
        {
            _softwareRendering = options.SoftwareRendering;
            _noComposition = options.NoComposition;
            if (options.ParentProcessId is { } pid && options.Executable is { } exe)
            {
                TryKillParent(pid);
                Thread.Sleep(StageSettleDelay);

                try
                {
#pragma warning disable RS0030
                    var whereAmI = Path.GetFullPath(Environment.ProcessPath!);
                    File.Copy(whereAmI, exe, true);
                    Thread.Sleep(StageSettleDelay);
                    var stageArgs = $"--pid \"{Environment.ProcessId}\" --prev \"{whereAmI}\" {RenderingFlags}".TrimEnd();
                    var startInfo = new ProcessStartInfo
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(exe)
                    };
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        startInfo.FileName = exe;
                        startInfo.Arguments = stageArgs;
                    }
                    else
                    {
                        startInfo.FileName = "setsid";
                        startInfo.Arguments = $"-f \"{exe}\" {stageArgs}";
                    }
                    Process.Start(startInfo);
                    Environment.Exit(0);
#pragma warning restore RS0030
                }
                catch (Exception e)
                {
                    LogService.WriteEarly($"Step 2 of update failed: {e}");
                    PartialUpdate = true;
                }
            }
            else if (options is { PreviousExecutablePath: { } previousExecutablePath, ParentProcessId: { } pidP })
            {
                TryKillParent(pidP);
                Thread.Sleep(StageSettleDelay);
                try
                {
#pragma warning disable RS0030
                    File.Delete(previousExecutablePath);
#pragma warning restore RS0030
                }
                catch (Exception e)
                {
                    LogService.WriteEarly($"Step 3 of update failed: {e}");
                    PartialUpdate = true;
                }
            }
        });
        try
        {
            var builder = BuildAvaloniaApp();
            if (CreateWin32Options(_softwareRendering, _noComposition) is { } win32Options)
            {
                builder = builder.With(win32Options);
            }
            Logger.Sink = new AvaloniaLogSink();
            builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            ReportCrash(e);
#pragma warning disable RS0030
            Environment.Exit(1);
#pragma warning restore RS0030
        }
    }

    internal static string GetRenderingFlags(bool softwareRendering, bool noComposition)
    {
        var flags = new List<string>();
        if (softwareRendering) flags.Add("--software-rendering");
        if (noComposition) flags.Add("--no-composition");
        return string.Join(' ', flags);
    }

    internal static Win32PlatformOptions? CreateWin32Options(bool softwareRendering, bool noComposition)
    {
        if (softwareRendering)
        {
            return new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            };
        }
        if (noComposition)
        {
            return new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface]
            };
        }
        return null;
    }

    private static int _crashReported;

    private static void ReportCrash(Exception e)
    {
        if (Interlocked.Exchange(ref _crashReported, 1) == 1) return;
        LogService.WriteEarly($"The launcher crashed: {e}");
        AvaloniaLogSink.Target?.Error("The launcher crashed.", e);
        App.ShowFatalError("KSP2 Redux closed because of an unexpected error", e);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new App())
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}