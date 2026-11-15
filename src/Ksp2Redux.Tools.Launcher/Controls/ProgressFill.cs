using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Ksp2Redux.Tools.Launcher.Controls;

/// <summary>
/// Lets a progress bar's fill ease toward a new value without easing a drop: the reset to 0 at the
/// start of an install would otherwise drain the old bar on screen.
/// </summary>
/// <remarks>
/// The easing itself is a Width transition on the fill in Styles.axaml, under the Motion setting.
/// Only the fill moves; the bar's text always shows the real value.
/// </remarks>
public static class ProgressFill
{
    private static bool _registered;

    /// <summary>Marks every falling progress value as instant. Safe to call more than once.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        RangeBase.ValueProperty.Changed.AddClassHandler<ProgressBar>((bar, change) =>
        {
            if (change.GetNewValue<double>() >= change.GetOldValue<double>()) return;
            bar.Classes.Add("instant");
            // After the layout pass that sizes the fill without its transition.
            Dispatcher.UIThread.Post(() => bar.Classes.Remove("instant"), DispatcherPriority.Background);
        });
    }
}
