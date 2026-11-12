using System.Globalization;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Localization;

public class LocalizationServiceTest
{
    private CultureInfo _uiCulture = null!;
    private CultureInfo _culture = null!;
    private CultureInfo? _defaultUiCulture;

    [SetUp]
    public void RememberCultures()
    {
        _uiCulture = CultureInfo.CurrentUICulture;
        _culture = CultureInfo.CurrentCulture;
        _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    }

    [TearDown]
    public void RestoreCultures()
    {
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    private static CultureInfo[] Available(params string[] names) =>
        names.Prepend("en").Select(CultureInfo.GetCultureInfo).ToArray();

    // The build offers English, German, Japanese, Brazilian Portuguese and Simplified Chinese.
    [TestCase("pt-BR", "en-US", "pt-BR", Description = "A configured language the build has wins")]
    [TestCase("PT-br", "en-US", "pt-BR", Description = "Names match without regard to case")]
    [TestCase("de-AT", "en-US", "de", Description = "A configured regional variant falls back to its neutral parent")]
    [TestCase("ja", "de-DE", "ja", Description = "The configured language wins over the operating system")]
    [TestCase("fr", "de-DE", "de", Description = "A configured language the build lacks follows the operating system")]
    [TestCase("not a culture", "ja-JP", "ja", Description = "A configured name that is no culture follows the operating system")]
    [TestCase("", "pt-BR", "pt-BR", Description = "Empty follows the operating system, exact match first")]
    [TestCase(null, "de-DE", "de", Description = "The operating system's regional variant falls back to its neutral parent")]
    [TestCase("", "zh-CN", "zh-Hans", Description = "A region maps through its script parent")]
    [TestCase("", "pt-PT", "en", Description = "A sibling region is no match, so English")]
    [TestCase("", "fr-FR", "en", Description = "An operating system language the build lacks gets English")]
    [TestCase("", "en-GB", "en", Description = "English variants get English")]
    public void ResolveCulture_PicksConfiguredThenSystemThenEnglish(string? configured, string system, string expected)
    {
        var culture = LocalizationService.ResolveCulture(
            configured, CultureInfo.GetCultureInfo(system), Available("de", "ja", "pt-BR", "zh-Hans"));

        Assert.That(culture.Name, Is.EqualTo(expected));
    }

    [Test]
    public void Indexer_MissingKey_ReturnsTheKeyAndWarnsOnce()
    {
        var log = new Mock<ILogService>();
        var service = new LocalizationService(new FakeStringSource().Add("en", "Known.Key", "Known"),
            CultureInfo.GetCultureInfo("en-US")) { Log = log.Object };

        Assert.That(service["No.Such.Key"], Is.EqualTo("No.Such.Key"));
        Assert.That(service["No.Such.Key"], Is.EqualTo("No.Such.Key"));

        log.Verify(l => l.Warn(It.Is<string>(message => message.Contains("No.Such.Key")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void Indexer_MissingTranslation_FallsBackToEnglish()
    {
        var service = new LocalizationService(
            new FakeStringSource()
                .Add("en", "Only.English", "english text")
                .Add("en", "Blank.Translation", "english blank")
                .Add("de", "Blank.Translation", "")
                .Add("de", "Other.Key", "de:other"),
            CultureInfo.GetCultureInfo("de-DE"));
        service.SetLanguage(null);

        Assert.That(service.Culture.Name, Is.EqualTo("de"));
        Assert.That(service["Only.English"], Is.EqualTo("english text"));
        Assert.That(service["Blank.Translation"], Is.EqualTo("english blank"), "A blank translation counts as missing");
    }

    [Test]
    public void Format_TranslationWithBrokenPlaceholder_FallsBackToEnglish()
    {
        var service = new LocalizationService(
            new FakeStringSource().Add("en", "Greeting", "hello {0}").Add("de", "Greeting", "de:hello {0} {1}"),
            CultureInfo.GetCultureInfo("de-DE"));
        service.SetLanguage(null);

        Assert.That(service.Format("Greeting", "world"), Is.EqualTo("hello world"));
    }

    [Test]
    public void SetLanguage_ChangesTextsAndNotifies()
    {
        var service = new LocalizationService(
            new FakeStringSource().Add("en", "Home.Tab", "Home").Add("de", "Home.Tab", "de:Home"),
            CultureInfo.GetCultureInfo("en-US"));
        var languageChanges = 0;
        var changedProperties = new List<string?>();
        service.LanguageChanged += (_, _) => languageChanges++;
        service.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
        var formattingCulture = CultureInfo.CurrentCulture;

        service.SetLanguage("de");

        Assert.That(service["Home.Tab"], Is.EqualTo("de:Home"));
        Assert.That(languageChanges, Is.EqualTo(1));
        Assert.That(changedProperties, Does.Contain(LocalizationService.INDEXER_NAME), "Indexer bindings listen for this name");
        Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("de"));
        Assert.That(CultureInfo.DefaultThreadCurrentUICulture?.Name, Is.EqualTo("de"));
        Assert.That(CultureInfo.CurrentCulture, Is.SameAs(formattingCulture), "Numbers and dates keep following the OS");

        service.SetLanguage("de");
        Assert.That(languageChanges, Is.EqualTo(1), "Choosing the same language again changes nothing");
    }

    [Test]
    public void AvailableLanguages_ListsEnglishAndEveryTranslationTheBuildHas()
    {
        var service = new LocalizationService(
            new FakeStringSource().Add("de", "Key", "de:text").Add("pt-BR", "Key", "pt:text").Add("xx", "Key", "not a contract culture"),
            CultureInfo.GetCultureInfo("en-US"));

        Assert.That(service.AvailableLanguages.Select(language => language.CultureName), Is.EqualTo(new[] { "en", "de", "pt-BR" }));
        Assert.That(service.AvailableLanguages[1].DisplayName, Is.EqualTo("Deutsch"));
        Assert.That(service.AvailableLanguages.All(language => char.IsUpper(language.DisplayName[0])), Is.True,
            "Every language name starts with a capital letter");
    }

    [Test]
    public void AvailableLanguages_WithoutTranslations_IsEnglishOnly()
    {
        var service = new LocalizationService(new FakeStringSource().Add("en", "Key", "text"), CultureInfo.GetCultureInfo("en-US"));

        Assert.That(service.AvailableLanguages.Select(language => language.CultureName), Is.EqualTo(new[] { "en" }));
    }
}
