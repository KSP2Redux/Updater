using System.Globalization;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Localization;

public class LogMessageTest
{
    private CultureInfo _uiCulture = null!;
    private CultureInfo? _defaultUiCulture;

    [SetUp]
    public void RememberCultures()
    {
        _uiCulture = CultureInfo.CurrentUICulture;
        _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    }

    [TearDown]
    public void RestoreCultures()
    {
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    private static LocalizationService German(FakeStringSource source)
    {
        var service = new LocalizationService(source, CultureInfo.GetCultureInfo("de-DE"));
        service.SetLanguage(null);
        return service;
    }

    [Test]
    public void Message_EnglishSideStaysEnglishWhateverTheLanguage()
    {
        var service = German(new FakeStringSource()
            .Add("en", "Greeting", "hello {0}").Add("de", "Greeting", "hallo {0}"));

        var inGerman = service.Message("Greeting", "world");
        service.SetLanguage("en");
        var inEnglish = service.Message("Greeting", "world");

        Assert.That(inGerman, Is.EqualTo(new LogMessage("hello world", "hallo world")));
        Assert.That(inEnglish, Is.EqualTo(new LogMessage("hello world", "hello world")));
    }

    [Test]
    public void Message_MissingOrBlankTranslation_LocalizedSideFallsBackToEnglish()
    {
        var service = German(new FakeStringSource()
            .Add("en", "Only.English", "english text")
            .Add("en", "Blank", "english blank").Add("de", "Blank", ""));

        Assert.That(service.Message("Only.English"), Is.EqualTo(new LogMessage("english text", "english text")));
        Assert.That(service.Message("Blank"), Is.EqualTo(new LogMessage("english blank", "english blank")));
    }

    [Test]
    public void Message_MissingKey_ReturnsTheKeyOnBothSides()
    {
        var service = German(new FakeStringSource().Add("en", "Known", "text"));

        Assert.That(service.Message("No.Such.Key"), Is.EqualTo(new LogMessage("No.Such.Key", "No.Such.Key")));
    }

    [Test]
    public void Message_TranslationWithBrokenPlaceholder_LocalizedSideFallsBackToEnglish()
    {
        var service = German(new FakeStringSource()
            .Add("en", "Greeting", "hello {0}").Add("de", "Greeting", "hallo {0} {1}"));

        Assert.That(service.Message("Greeting", "world"), Is.EqualTo(new LogMessage("hello world", "hello world")));
    }

    [Test]
    public void Message_NestedLogMessage_ExpandsOnEachSide()
    {
        var service = German(new FakeStringSource()
            .Add("en", "Wrapper", "failed: {0}").Add("de", "Wrapper", "Fehler: {0}"));
        var inner = new LogMessage("disk full", "Platte voll");

        Assert.That(service.Message("Wrapper", inner), Is.EqualTo(new LogMessage("failed: disk full", "Fehler: Platte voll")));
    }

    [Test]
    public void Message_ReorderedPlaceholders_FillEachLanguageInItsOwnOrder()
    {
        var service = German(new FakeStringSource()
            .Add("en", "Progress", "{0} of {1}").Add("de", "Progress", "{1} von {0}"));

        Assert.That(service.Message("Progress", "a", "b"), Is.EqualTo(new LogMessage("a of b", "b von a")));
    }

    [Test]
    public void Literal_GivesTheSameTextOnBothSides()
    {
        var message = LogMessage.Literal("process output");

        Assert.That(message.English, Is.EqualTo("process output"));
        Assert.That(message.Localized, Is.EqualTo("process output"));
    }

    [Test]
    public void ToString_AndInterpolation_GiveTheEnglishText()
    {
        var message = new LogMessage("failed", "fehlgeschlagen");

        Assert.That(message.ToString(), Is.EqualTo("failed"));
        Assert.That($"{message}", Is.EqualTo("failed"));
    }

    [Test]
    public void ToException_UsesEnglishAsMessageAndKeepsTheLocalizedTextForTheUser()
    {
        var exception = new LogMessage("disk full", "Platte voll").ToException(text => new IOException(text));

        Assert.That(exception.Message, Is.EqualTo("disk full"));
        Assert.That(exception.UserMessage(), Is.EqualTo("Platte voll"));
        Assert.That(exception.ToString(), Does.Contain("disk full").And.Not.Contain("Platte voll"));
    }

    [Test]
    public void UserMessage_ForeignException_FallsBackToTheMessage()
    {
        Assert.That(new InvalidOperationException("framework text").UserMessage(), Is.EqualTo("framework text"));
    }

    [Test]
    public void ToLogMessage_CarriesBothTextsOfALauncherException()
    {
        var launcher = new LogMessage("disk full", "Platte voll").ToException(text => new IOException(text));

        Assert.That(launcher.ToLogMessage(), Is.EqualTo(new LogMessage("disk full", "Platte voll")));
        Assert.That(new InvalidOperationException("framework text").ToLogMessage(),
            Is.EqualTo(LogMessage.Literal("framework text")));
    }

    [Test]
    public void Message_WrappingAnInnerException_NestsBothTexts()
    {
        var service = German(new FakeStringSource()
            .Add("en", "Install.StepFailed", "Step failed: {0}").Add("de", "Install.StepFailed", "Schritt fehlgeschlagen: {0}"));
        var inner = new LogMessage("disk full", "Platte voll").ToException(text => new IOException(text));

        var wrapped = service.Message("Install.StepFailed", inner.ToLogMessage());

        Assert.That(wrapped, Is.EqualTo(new LogMessage("Step failed: disk full", "Schritt fehlgeschlagen: Platte voll")));
    }

    [Test]
    public void Describe_CapturedMessages_AreTheEnglishResourceTextOnTheEnglishSide()
    {
        // The service reads the shared service, so this runs in English. The English side must still be exactly
        // the neutral resource text with the arguments filled in, which is what the log file receives.
        var plan = new InstallPlan();
        plan.Uninstall();
        plan.Prepatch();
        List<LogMessage> captured = [];

        new InstallPlanService(new MockFileSystem(), Mock.Of<ICacheService>(), Mock.Of<IEnvironmentProvider>(),
                Mock.Of<IAssemblyService>(), Mock.Of<IModuleDefinitionService>(), Mock.Of<IZipFileService>(),
                Mock.Of<IDiskSpaceService>(), Mock.Of<IPatchDownloadService>(), Mock.Of<ILauncherConfigService>())
            .Describe(plan, captured.Add);

        var neutral = new ResourceStringSource();
        string Expected(string key, int step) =>
            string.Format(CultureInfo.CurrentCulture, neutral.GetString(key, CultureInfo.GetCultureInfo("en"))!, step);
        // Prepatch() was added last, so it is step 1.
        Assert.That(captured.Select(line => line.English), Is.EqualTo(new[]
        {
            Expected("Install.PlanMade", 2),
            Expected("Install.Step.Prepatch", 1),
            Expected("Install.Step.Uninstall", 2)
        }));
    }

    [Test]
    public async Task SteamDownloadViewModel_MissingExe_ShowsGermanButThrowsAndLogsEnglish()
    {
        var localization = German(new FakeStringSource()
            .Add("en", "Steam.Download.MissingExe", "{0} is missing from the download.")
            .Add("de", "Steam.Download.MissingExe", "{0} fehlt im Download."));
        var log = new Mock<ILogService>();
        Exception? logged = null;
        log.Setup(l => l.Error(It.IsAny<string>(), It.IsAny<Exception?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string _, Exception? exception, string _, string _) => logged = exception);
        var viewModel = new SteamDownloadViewModel(@"C:\game", Mock.Of<ISteamDepotDownloader>(),
            Mock.Of<IKsp2InstallService>(), new MockFileSystem(), log.Object, localization);

        await viewModel.StartAsync();

        Assert.That(viewModel.ErrorMessage, Is.EqualTo($"{Ksp2Install.KSP2_EXE_NAME} fehlt im Download."));
        Assert.That(logged!.Message, Is.EqualTo($"{Ksp2Install.KSP2_EXE_NAME} is missing from the download."));
        Assert.That(logged.UserMessage(), Is.EqualTo(viewModel.ErrorMessage));
    }
}
