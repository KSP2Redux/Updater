using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Ksp2Redux.Tools.Launcher.Controls;

namespace Ksp2Redux.Tools.Launcher.Converters;

public class SelectableConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ISelectableItem si)
        {
            return si.IsSelectable;
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}