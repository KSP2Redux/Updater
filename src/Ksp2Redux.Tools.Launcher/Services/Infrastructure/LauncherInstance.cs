using System.Runtime.InteropServices;

namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

public static class LauncherInstance
{
    private const string MUTEX_NAME = "Ksp2Redux.Launcher.Running";

    // Off Windows a named mutex is scoped to the Unix session by default, so a launcher opened from
    // Finder or a desktop menu and the CLI run from a terminal would not see each other.
    private static readonly NamedWaitHandleOptions UnixScope = new() { CurrentSessionOnly = false };
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static Mutex? _marker;

    /// <summary>Marks this process as the running launcher.</summary>
    /// <returns>False if another launcher had already marked itself running.</returns>
    public static bool MarkRunning()
    {
        if (_marker is not null) return true;
        bool createdNew;
        _marker = IsWindows
            ? new Mutex(false, MUTEX_NAME, out createdNew)
            : new Mutex(false, MUTEX_NAME, UnixScope, out createdNew);
        return createdNew;
    }

    public static bool IsRunning()
    {
        Mutex? existing;
        var found = IsWindows
            ? Mutex.TryOpenExisting(MUTEX_NAME, out existing)
            : Mutex.TryOpenExisting(MUTEX_NAME, UnixScope, out existing);
        existing?.Dispose();
        return found;
    }
}
