using System.Globalization;
using Avalonia.Data.Converters;
using Ksp2Redux.Tools.Launcher.ViewModels;

namespace Ksp2Redux.Tools.Launcher.Converters;

public class IsSettingsTabConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue == MainWindowViewModel.SettingsTabId;

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}