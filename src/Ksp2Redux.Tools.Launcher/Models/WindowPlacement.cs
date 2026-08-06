namespace Ksp2Redux.Tools.Launcher.Models;

/// <summary>
/// Persisted window geometry. X/Y are physical screen pixels (Avalonia's Window.Position),
/// Width/Height are logical units (Window.Width/Height) - the mix mirrors what Avalonia
/// itself exposes. When the window closes maximized, Width/Height/X/Y hold the last
/// NORMAL bounds so a restore-from-maximized lands where the user left the window.
/// </summary>
public class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}
