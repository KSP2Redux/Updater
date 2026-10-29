using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Localization;

public sealed class LocalizationService : ILocalizationService
{
    // The cultures the translation pipeline writes Strings.<culture>.resx for. One only shows up as a
    // language once its file is actually in the build.
    internal static readonly string[] TranslationCultures =
        ["de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ro", "ru", "sr", "sv", "zh-Hans", "zh-Hant"];

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");

    // What Avalonia's indexer bindings listen for. Unlike WPF, it is the indexer's own name, not "Item[]".
    internal const string INDEXER_NAME = "Item";

    private readonly IStringSource _source;
    private readonly CultureInfo _systemCulture;
    private readonly CultureInfo[] _availableCultures;
    private readonly ConcurrentDictionary<string, bool> _warned = new();

    /// <summary>
    /// The one instance the app and its markup share. The application's XAML loads before the container
    /// exists, so the markup cannot wait for one. It reads English until a language is set.
    /// </summary>
    public static LocalizationService Default { get; } = new(new ResourceStringSource());

    public LocalizationService(IStringSource source, CultureInfo? systemCulture = null)
    {
        _source = source;
        _systemCulture = systemCulture ?? CultureInfo.CurrentUICulture;
        Culture = EnglishCulture;

        var languages = new List<LanguageOption> { ToOption(EnglishCulture) };
        languages.AddRange(TranslationCultures
            .Select(CultureInfo.GetCultureInfo)
            .Where(source.HasTranslation)
            .Select(ToOption));
        AvailableLanguages = languages;
        _availableCultures = languages.Select(language => CultureInfo.GetCultureInfo(language.CultureName)).ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    /// <summary>Where missing keys are reported. Set once the container has a logger.</summary>
    public ILogService? Log { get; set; }

    public CultureInfo Culture { get; private set; }

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    public string this[string key]
    {
        get
        {
            var culture = Culture;
            var text = _source.GetString(key, culture);
            if (string.IsNullOrEmpty(text) && culture.Name != EnglishCulture.Name)
            {
                text = _source.GetString(key, EnglishCulture);
            }

            if (!string.IsNullOrEmpty(text)) return text;
            WarnOnce($"missing:{key}", $"No localized text for key '{key}'.");
            return key;
        }
    }

    public string Format(string key, params object?[] args)
    {
        var text = this[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder must not take the dialog down with it.
            WarnOnce($"format:{Culture.Name}:{key}", $"The text for key '{key}' in {Culture.Name} has an invalid format.");
            var english = _source.GetString(key, EnglishCulture);
            return english is null ? text : string.Format(CultureInfo.CurrentCulture, english, args);
        }
    }

    public LogMessage Message(string key, params object?[] args)
    {
        var english = _source.GetString(key, EnglishCulture);
        if (string.IsNullOrEmpty(english)) english = key;
        string englishText;
        try
        {
            englishText = string.Format(CultureInfo.CurrentCulture, english, SideArguments(args, message => message.English));
        }
        catch (FormatException)
        {
            englishText = english;
        }

        return new LogMessage(englishText, Format(key, SideArguments(args, message => message.Localized)));
    }

    private static object?[] SideArguments(object?[] args, Func<LogMessage, string> side) =>
        args.Select(arg => arg is LogMessage message ? side(message) : arg).ToArray();

    public void SetLanguage(string? cultureName)
    {
        var culture = ResolveCulture(cultureName, _systemCulture, _availableCultures);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        if (culture.Name == Culture.Name) return;

        Culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(INDEXER_NAME));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Picks the language to use: the configured one when the build has it, otherwise the operating
    /// system's, otherwise English. Each is matched exactly first, then through its parent cultures.
    /// </summary>
    internal static CultureInfo ResolveCulture(string? configured, CultureInfo system, IReadOnlyCollection<CultureInfo> available)
    {
        if (!string.IsNullOrWhiteSpace(configured) && TryParse(configured) is { } requested &&
            Match(requested, available) is { } chosen)
        {
            return chosen;
        }

        return Match(system, available) ?? EnglishCulture;
    }

    private static CultureInfo? Match(CultureInfo culture, IReadOnlyCollection<CultureInfo> available)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            var match = available.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, current.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }

    private static CultureInfo? TryParse(string cultureName)
    {
        try
        {
            return CultureInfo.GetCultureInfo(cultureName.Trim());
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static LanguageOption ToOption(CultureInfo culture)
    {
        var name = culture.NativeName;
        return new LanguageOption(culture.Name, name.Length == 0 ? name : char.ToUpper(name[0], culture) + name[1..]);
    }

    private void WarnOnce(string id, string message)
    {
        if (_warned.TryAdd(id, true)) Log?.Warn(message);
    }
}
