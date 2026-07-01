using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CommandLine;
using Ksp2Redux.Tools.Launcher.Services;

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
    }

    public static bool PartialUpdate { get; private set; }

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
        Parser.Default.ParseArguments<Options>(args).WithParsed(options =>
        {
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
                    var stageArgs = $"--pid \"{Environment.ProcessId}\" --prev \"{whereAmI}\"";
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
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
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