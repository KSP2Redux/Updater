using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ksp2Redux.Tools.Launcher.Localization;

/// <summary>
/// The fonts of the launcher for each language. JetBrains Mono and LED Counter 7 have no Chinese, Japanese
/// or Korean glyphs, and LED Counter 7 also lacks Polish and Cyrillic letters, so those languages get fonts
/// that cover them. Chinese and Japanese share code points but draw them differently, so each of these
/// languages lists its own font first. The fonts are built by scripts/fonts/build-language-fonts.py.
/// </summary>
public static class LanguageFonts
{
    public const string LIGHT_KEY = "JetBrainsMonoLight";
    public const string REGULAR_KEY = "JetBrainsMonoRegular";
    public const string BOLD_KEY = "JetBrainsMonoBold";
    public const string LED_KEY = "LedCounter7";

    private const string FONTS = "avares://Ksp2Redux.Tools.Launcher/Assets/Fonts";
    private const string LANGUAGE_FONTS = "avares://Ksp2Redux.Tools.Launcher/Assets/LanguageFonts";

    // Each folder is a font collection of its own, so a language's fonts are only loaded when used.
    private static readonly (string Culture, string Body, string? Led)[] CjkFonts =
    [
        ("ja", $"{LANGUAGE_FONTS}/ja#Noto Sans JP", $"{LANGUAGE_FONTS}/ja#Redux Dot JP"),
        ("zh-Hans", $"{LANGUAGE_FONTS}/zh-Hans#Noto Sans SC", $"{LANGUAGE_FONTS}/zh-Hans#Z Labs RoundPix 12px MD CN"),
        ("zh-Hant", $"{LANGUAGE_FONTS}/zh-Hant#Noto Sans TC", $"{LANGUAGE_FONTS}/zh-Hant#Redux Dot TC"),
        ("ko", $"{LANGUAGE_FONTS}/ko#Noto Sans KR", $"{LANGUAGE_FONTS}/ko#Redux Dot KR")
    ];

    // LED Counter 7 lacks these letters. A whole label in one dot font reads better than one word mixing
    // two dot grids, so these replace it instead of backing it up.
    private static readonly (string Culture, string Led)[] WholeLabelLedFonts =
    [
        ("pl", $"{LANGUAGE_FONTS}/pl#Doto Round"),
        ("ru", $"{LANGUAGE_FONTS}/ru#DotMatrix")
    ];

    /// <summary>Sets the font resources of the launcher to the ones for <paramref name="culture"/>.</summary>
    public static void Apply(IResourceDictionary resources, CultureInfo culture)
    {
        foreach (var (key, fonts) in FontLists(culture)) resources[key] = Family(fonts);
    }

    /// <summary>
    /// The body font for text written in <paramref name="cultureName"/>, such as a language's own name in the
    /// language picker. Null when JetBrains Mono covers the language by itself.
    /// </summary>
    public static FontFamily? BodyFontFor(string cultureName)
    {
        if (string.IsNullOrEmpty(cultureName)) return null;
        var culture = CultureInfo.GetCultureInfo(cultureName);
        return CjkFallbacks(culture).Length == 0 ? null : Family(FontLists(culture)[REGULAR_KEY]);
    }

    /// <summary>Each font resource with the fonts it lists for <paramref name="culture"/>, first one first.</summary>
    internal static IReadOnlyDictionary<string, string[]> FontLists(CultureInfo culture)
    {
        var cjk = CjkFallbacks(culture);
        return new Dictionary<string, string[]>
        {
            [LIGHT_KEY] = [$"{FONTS}#JetBrains Mono Light", ..cjk],
            [REGULAR_KEY] = [$"{FONTS}#JetBrains Mono Regular", ..cjk],
            [BOLD_KEY] = [$"{FONTS}#JetBrains Mono Bold", ..cjk.Select(font => font + " Bold")],
            [LED_KEY] = LedFonts(culture)
        };
    }

    private static string[] LedFonts(CultureInfo culture)
    {
        if (Find(WholeLabelLedFonts, culture, font => font.Culture) is { } whole) return [whole.Led];
        if (Find(CjkFonts, culture, font => font.Culture) is { Led: { } led }) return [$"{FONTS}#LED Counter 7", led];
        return [$"{FONTS}#LED Counter 7", $"{FONTS}#Handjet"];
    }

    /// <summary>
    /// The CJK fonts to fall back to: the culture's own first, then the others for characters it lacks.
    /// Empty for other languages, which keeps the large CJK fonts unloaded for them.
    /// </summary>
    internal static string[] CjkFallbacks(CultureInfo culture)
    {
        if (Find(CjkFonts, culture, font => font.Culture) is not { } own) return [];
        return CjkFonts.Where(font => font.Culture == own.Culture)
            .Concat(CjkFonts.Where(font => font.Culture != own.Culture))
            .Select(font => font.Body)
            .ToArray();
    }

    private static FontFamily Family(IEnumerable<string> fonts) => new(string.Join(", ", fonts));

    private static T? Find<T>(T[] fonts, CultureInfo culture, Func<T, string> cultureOf) where T : struct
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            foreach (var font in fonts)
            {
                if (string.Equals(cultureOf(font), current.Name, StringComparison.OrdinalIgnoreCase)) return font;
            }
        }

        return null;
    }
}
