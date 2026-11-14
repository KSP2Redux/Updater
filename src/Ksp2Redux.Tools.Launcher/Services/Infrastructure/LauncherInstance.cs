namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

public static class LauncherInstance
{
    private const string MUTEX_NAME = "Ksp2Redux.Launcher.Running";

    private static Mutex? _marker;

    public static void MarkRunning() => _marker ??= new Mutex(false, MUTEX_NAME);

    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MUTEX_NAME, out var existing))
        {
            return false;
        }

        existing.Dispose();
        return true;
    }
}
