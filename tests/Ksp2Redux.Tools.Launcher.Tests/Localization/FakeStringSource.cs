using System.Globalization;
using Ksp2Redux.Tools.Launcher.Localization;

namespace Ksp2Redux.Tools.Launcher.Tests.Localization;

/// <summary>
/// Stands in for the embedded resources, so tests can offer languages without translation files.
/// Looks texts up the way ResourceManager does: the culture, its parents, then the neutral English.
/// </summary>
internal sealed class FakeStringSource : IStringSource
{
    private readonly Dictionary<string, Dictionary<string, string>> _texts = new(StringComparer.OrdinalIgnoreCase);

    public FakeStringSource Add(string culture, string key, string value)
    {
        if (!_texts.TryGetValue(culture, out var texts))
        {
            _texts[culture] = texts = new Dictionary<string, string>();
        }

        texts[key] = value;
        return this;
    }

    public string? GetString(string key, CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (_texts.TryGetValue(current.Name, out var texts) && texts.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return _texts.TryGetValue("en", out var neutral) && neutral.TryGetValue(key, out var english) ? english : null;
    }

    public bool HasTranslation(CultureInfo culture) => culture.Name != "en" && _texts.ContainsKey(culture.Name);
}
