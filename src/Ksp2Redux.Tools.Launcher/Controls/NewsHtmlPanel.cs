using System.Globalization;
using Avalonia;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace Ksp2Redux.Tools.Launcher.Controls;

/// <summary>
/// Render news without the HTML renderer interpreting CSS delimiters using the user's culture.
/// </summary>
public class NewsHtmlPanel : HtmlPanel
{
    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != BaseStylesheetProperty && change.Property != TextProperty)
        {
            base.OnPropertyChanged(change);
            return;
        }

        // Avalonia.HtmlRenderer 12.0.0 uses culture-sensitive CSS comment searches.
        // Remove this workaround once the dependency includes the upstream fix.
        // https://github.com/AvaloniaUI/Avalonia.HtmlRenderer/pull/84
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            base.OnPropertyChanged(change);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
