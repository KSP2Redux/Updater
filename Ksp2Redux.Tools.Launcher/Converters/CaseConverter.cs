using System;
using Avalonia.Data.Converters;

namespace Ksp2Redux.Tools.Launcher.Converters;

public class CaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string str || parameter is not string casing)
        {
            return value;
        }

        return casing switch
        {
            "Lower" => str.ToLower(),
            "Normal" => str,
            "Upper" => str.ToUpper(),
            _ => str
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}