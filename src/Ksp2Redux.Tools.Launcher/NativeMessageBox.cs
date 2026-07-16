using System.Runtime.InteropServices;

namespace Ksp2Redux.Tools.Launcher;

/// <summary>
/// A plain Win32 message box with zero managed dependencies, for reporting failures so early
/// (e.g. the DI container itself failing to build) that Avalonia's own dialog stack may not be
/// safe to use yet.
/// </summary>
internal static class NativeMessageBox
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void ShowError(string text, string caption)
    {
        MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR);
    }
}
