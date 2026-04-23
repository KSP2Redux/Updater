using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommandLine;

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
                try
                {
                    var process = Process.GetProcessById(pid);
                    process.Kill(false);
                    process.WaitForExit();
                }
                catch (ArgumentException)
                {
                    // The parent process already is dead here
                }

#pragma warning disable RS0030
                var whereAmI = Path.GetFullPath(Environment.ProcessPath!);
                File.Copy(whereAmI, exe, true);
                var startInfo = new ProcessStartInfo(exe,
                    $"--pid \"{Environment.ProcessId}\" --prev \"{whereAmI}\"")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(startInfo);
                Environment.Exit(0);
#pragma warning restore RS0030
            }
            else if (options is { PreviousExecutablePath: { } previousExecutablePath, ParentProcessId: { } pidP })
            {
                try
                {
                    var process = Process.GetProcessById(pidP);
                    process.Kill(false);
                    process.WaitForExit();
                }
                catch (ArgumentException)
                {
                    // The parent process already is dead here
                }
#pragma warning disable RS0030
                File.Delete(previousExecutablePath);
#pragma warning restore RS0030
            }
        });
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}