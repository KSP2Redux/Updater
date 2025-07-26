using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Ksp2Redux.Tools.Launcher.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public IBrush? HeaderBrush { get; set; } = Brushes.Gray;
    public IBrush? ItemBrush { get; set; } = Brushes.Black;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isHeader)
            return isHeader ? HeaderBrush : ItemBrush;

        return ItemBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}