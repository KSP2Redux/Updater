using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Ksp2Redux.Tools.Launcher.Localization;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

/// <summary>
/// Checks that the fonts each language lists are bundled under the names used, and that the translations the
/// pipeline delivers can be drawn with them, so no text falls back to whatever the operating system has.
/// </summary>
public class LanguageFontsTest
{
    // The main buttons, which show their label in the LED font.
    private static readonly string[] LedKeys =
        ["Sidebar.InstallButton", "Sidebar.UpdateButton", "Sidebar.CancelButton", "Sidebar.LaunchButton"];

    private static readonly ResourceManager Strings =
        new("Ksp2Redux.Tools.Launcher.Localization.Strings", typeof(LocalizationService).Assembly);

    private static IEnumerable<string> AllCultures() => LocalizationService.TranslationCultures.Prepend("en");

    private static IEnumerable<string> TranslatedCultures() => LocalizationService.TranslationCultures
        .Where(name => Strings.GetResourceSet(CultureInfo.GetCultureInfo(name), true, false) is not null);

    [AvaloniaTest]
    public void EveryListedFont_IsBundledUnderThatName([ValueSource(nameof(AllCultures))] string cultureName)
    {
        List<string> problems = [];
        foreach (var (key, fonts) in LanguageFonts.FontLists(CultureInfo.GetCultureInfo(cultureName)))
        {
            foreach (var font in fonts)
            {
                var name = font[(font.IndexOf('#') + 1)..];
                var typeface = Resolve(font);
                if (typeface is null || !name.StartsWith(typeface.FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{key}: {font} resolved to {typeface?.FamilyName ?? "nothing"}");
                }
                else if (name.EndsWith(" Bold") && typeface.Weight != FontWeight.Bold)
                {
                    problems.Add($"{key}: {font} resolved to weight {typeface.Weight}");
                }
            }
        }

        Assert.That(problems, Is.Empty);
    }

    [AvaloniaTest]
    public void EveryTranslatedText_HasGlyphsInItsFonts([ValueSource(nameof(TranslatedCultures))] string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var lists = LanguageFonts.FontLists(culture);
        var body = lists[LanguageFonts.REGULAR_KEY].Select(Resolve).OfType<GlyphTypeface>().ToArray();
        var led = lists[LanguageFonts.LED_KEY].Select(Resolve).OfType<GlyphTypeface>().ToArray();
        List<string> missing = [];

        foreach (DictionaryEntry entry in Strings.GetResourceSet(culture, true, false)!)
        {
            var key = (string)entry.Key;
            if (entry.Value is not string text) continue;
            var fonts = LedKeys.Contains(key) ? led : body;
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) continue;
                if (!fonts.Any(font => font.CharacterToGlyphMap.ContainsGlyph(rune.Value)))
                {
                    missing.Add($"{key}: '{rune}' U+{rune.Value:X4}");
                }
            }
        }

        Assert.That(missing.Distinct(), Is.Empty, $"No {cultureName} font has these characters");
    }

    [Test]
    public void ChineseAndJapanese_ListTheirOwnFontFirst()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FirstFallback("ja"), Does.EndWith("#Noto Sans JP"));
            Assert.That(FirstFallback("zh-Hans"), Does.EndWith("#Noto Sans SC"));
            Assert.That(FirstFallback("zh-Hant"), Does.EndWith("#Noto Sans TC"));
            Assert.That(FirstFallback("zh-TW"), Does.EndWith("#Noto Sans TC"));
            Assert.That(FirstFallback("ko"), Does.EndWith("#Noto Sans KR"));
            Assert.That(LanguageFonts.FontLists(CultureInfo.GetCultureInfo("de"))[LanguageFonts.REGULAR_KEY],
                Has.Length.EqualTo(1), "Languages JetBrains Mono covers load no CJK font");
        });
    }

    private static string FirstFallback(string cultureName) =>
        LanguageFonts.FontLists(CultureInfo.GetCultureInfo(cultureName))[LanguageFonts.REGULAR_KEY][1];

    private static GlyphTypeface? Resolve(string font) =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(font)), out var typeface) ? typeface : null;
}
