using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Ksp2Redux.Tools.Launcher.Localization;

/// <summary>
/// Looks a text up by key and keeps it up to date when the language changes: <c>{loc:T Home.LaunchButton}</c>.
/// </summary>
public class TExtension : MarkupExtension
{
    /// <summary>The service the markup reads from.</summary>
    public static ILocalizationService Service { get; set; } = LocalizationService.Default;

    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    // The window's bindings are compiled, but a binding made here has no data type to compile against,
    // so it goes through reflection on the service's indexer.
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBindingExtension($"[{Key}]")
        {
            Source = Service,
            Mode = BindingMode.OneWay
        }.ProvideValue(serviceProvider);
}
