using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Ksp2Redux.Tools.Launcher.Localization;

namespace Ksp2Redux.Tools.Launcher.Converters;

/// <summary>
/// Gives a culture name the font its language is written in, so the language picker shows each language's
/// own name in the right glyphs whatever the current language is. Languages the current font covers keep it.
/// </summary>
public class LanguageFontConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string cultureName && LanguageFonts.BodyFontFor(cultureName) is { } font
            ? font
            : AvaloniaProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
