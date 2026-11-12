using System.Globalization;
using System.Resources;

namespace Ksp2Redux.Tools.Launcher.Localization;

/// <summary>Where the localization service reads its texts from.</summary>
public interface IStringSource
{
    /// <summary>Gets a text, falling back to the neutral English one. Null when the key does not exist.</summary>
    string? GetString(string key, CultureInfo culture);

    /// <summary>True when the build carries a translation file for exactly this culture.</summary>
    bool HasTranslation(CultureInfo culture);
}

/// <summary>Reads the texts from the embedded Strings.resx and its satellite assemblies.</summary>
public sealed class ResourceStringSource : IStringSource
{
    private readonly ResourceManager _resources =
        new("Ksp2Redux.Tools.Launcher.Localization.Strings", typeof(ResourceStringSource).Assembly);

    public string? GetString(string key, CultureInfo culture) => _resources.GetString(key, culture);

    public bool HasTranslation(CultureInfo culture)
    {
        try
        {
            return _resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false) is not null;
        }
        catch (Exception ex) when (ex is MissingManifestResourceException or MissingSatelliteAssemblyException
                                       or FileNotFoundException)
        {
            return false;
        }
    }
}
