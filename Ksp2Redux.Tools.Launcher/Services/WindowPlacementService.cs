using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IWindowPlacementService
{
    /// <summary>
    /// Validates a saved placement against the CURRENT monitor layout and returns a safe
    /// one to apply, or null when there's nothing usable and the caller should fall back
    /// to the built-in defaults (CenterScreen). Monitors get unplugged and resolutions
    /// change between runs, so the saved values can't be trusted blindly - a window
    /// restored onto a screen that no longer exists would be unreachable.
    /// </summary>
    WindowPlacement? Sanitize(WindowPlacement? saved, IReadOnlyList<PixelRect> screenWorkingAreas, double minWidth, double minHeight);
}

public class WindowPlacementService : IWindowPlacementService
{
    // How much of the title-bar strip must remain on some screen for the window to count
    // as grabbable. Generous on purpose: the goal is only to prevent a stranded window,
    // not to force it fully on-screen (half-off-screen is a legitimate user choice).
    private const int MinVisiblePx = 48;

    public WindowPlacement? Sanitize(WindowPlacement? saved, IReadOnlyList<PixelRect> screenWorkingAreas, double minWidth, double minHeight)
    {
        if (saved is null) return null;
        if (!IsSane(saved.Width) || !IsSane(saved.Height)) return null;

        var result = new WindowPlacement
        {
            X = saved.X,
            Y = saved.Y,
            Width = Math.Max(saved.Width, minWidth),
            Height = Math.Max(saved.Height, minHeight),
            IsMaximized = saved.IsMaximized,
        };

        // No screen info available (headless, exotic platform): nothing to validate against.
        if (screenWorkingAreas.Count == 0) return result;

        // The size is logical units and the areas are physical pixels, so on scaled
        // displays this rect is an approximation - fine for a "did the monitor
        // disappear?" check, which only needs to be roughly right.
        var window = new PixelRect(result.X, result.Y, (int)result.Width, (int)result.Height);
        var titleStrip = new PixelRect(window.X, window.Y, window.Width, MinVisiblePx);

        var grabbable = screenWorkingAreas.Any(area =>
        {
            var overlap = area.Intersect(titleStrip);
            return overlap.Width >= MinVisiblePx && overlap.Height >= Math.Min(MinVisiblePx, titleStrip.Height);
        });
        if (grabbable) return result;

        // The saved spot no longer exists (monitor unplugged, resolution shrank). Nudge
        // the window into the nearest working area rather than discarding the size the
        // user chose.
        var nearest = screenWorkingAreas
            .OrderBy(area => Distance(area, window))
            .First();
        result.X = Math.Clamp(window.X, nearest.X, Math.Max(nearest.X, nearest.Right - window.Width));
        result.Y = Math.Clamp(window.Y, nearest.Y, Math.Max(nearest.Y, nearest.Bottom - MinVisiblePx));
        return result;
    }

    private static bool IsSane(double dimension) =>
        double.IsFinite(dimension) && dimension > 0 && dimension < 100_000;

    private static double Distance(PixelRect area, PixelRect window)
    {
        var dx = Math.Max(0, Math.Max(area.X - window.Right, window.X - area.Right));
        var dy = Math.Max(0, Math.Max(area.Y - window.Bottom, window.Y - area.Bottom));
        return dx * dx + (double)dy * dy;
    }
}
