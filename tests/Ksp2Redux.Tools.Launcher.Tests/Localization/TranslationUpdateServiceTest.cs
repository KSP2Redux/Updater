using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Localization;

public class TranslationUpdateServiceTest
{
    private const string STORAGE = "C:/storage";
    private const string BASE_URL = "https://example.test/Localization";

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

    [Test]
    public void SelectUsable_KeepsOnlyTextsMadeForThisLaunchersEnglish()
    {
        var builtIn = new Dictionary<string, string>
        {
            ["Same"] = "Launch",
            ["Changed"] = "Install the game",
            ["Format"] = "{0} of {1} files",
            ["Blank"] = "Close",
        };
        var remoteEnglish = new Dictionary<string, string>
        {
            ["Same"] = "Launch",
            ["Changed"] = "Install Redux",
            ["Format"] = "{0} of {1} files",
            ["Blank"] = "Close",
            ["Newer"] = "A key this launcher does not have",
        };
        var translation = new Dictionary<string, string>
        {
            ["Same"] = "Starten",
            ["Changed"] = "Redux installieren",
            ["Format"] = "{0} Dateien",
            ["Blank"] = " ",
            ["Newer"] = "Neu",
            ["Stale"] = "Not in the remote English at all",
        };

        var usable = TranslationUpdateService.SelectUsable(remoteEnglish, translation,
            key => builtIn.GetValueOrDefault(key));

        Assert.That(usable, Is.EqualTo(new Dictionary<string, string> { ["Same"] = "Starten" }),
            "Changed English, broken placeholders, blank texts and unknown keys are all left out");
    }

    [Test]
    public async Task Update_DownloadedLanguageAppearsAndItsTextsWin()
    {
        var remote = new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch"), ("Home.Status", "{0} files")),
            ["Strings.fr.resx"] = Resx(("Home.Launch", "Lancer"), ("Home.Status", "{0} fichiers")),
        };
        var (service, _, files, _) = Create(remote);

        Assert.That(await service.DownloadAsync(), Is.True);
        var (restarted, localization, _, _) = Create(remote, files: files);
        restarted.ApplyCached();
        localization.SetLanguage("fr");

        Assert.That(localization.AvailableLanguages.Select(language => language.CultureName), Does.Contain("fr"),
            "A language the build lacks becomes selectable once downloaded");
        Assert.That(localization["Home.Launch"], Is.EqualTo("Lancer"));
        Assert.That(localization.Format("Home.Status", 3), Is.EqualTo("3 fichiers"));
    }

    [Test]
    public async Task Update_SecondRunSendsTheETagAndKeepsTheCacheOn304()
    {
        var remote = new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch")),
            ["Strings.fr.resx"] = Resx(("Home.Launch", "Lancer")),
        };
        var (service, _, files, _) = Create(remote);
        await service.DownloadAsync();
        var (restarted, localization, _, requests) = Create(remote, files: files);

        Assert.That(await restarted.DownloadAsync(), Is.False, "Nothing changed");
        Assert.That(requests.Where(request => request.Headers.IfNoneMatch.Count > 0).Select(request => request.RequestUri!.Segments[^1]),
            Is.EquivalentTo(new[] { "Strings.resx", "Strings.fr.resx" }));
        restarted.ApplyCached();
        localization.SetLanguage("fr");
        Assert.That(localization["Home.Launch"], Is.EqualTo("Lancer"));
    }

    [Test]
    public async Task Update_LanguageRemovedUpstreamIsDropped()
    {
        var remote = new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch")),
            ["Strings.fr.resx"] = Resx(("Home.Launch", "Lancer")),
        };
        var (service, localization, files, _) = Create(remote);
        await service.DownloadAsync();
        service.ApplyCached();
        Assert.That(localization.AvailableLanguages.Select(language => language.CultureName), Does.Contain("fr"));

        remote.Remove("Strings.fr.resx");
        Assert.That(await service.DownloadAsync(), Is.True);
        var (restarted, restoredLocalization, _, _) = Create(remote, files: files);
        restarted.ApplyCached();
        Assert.That(restoredLocalization.AvailableLanguages.Select(language => language.CultureName), Does.Not.Contain("fr"));
    }

    [TestCase("{\uFF10} fichiers")]
    [TestCase("{2147483648} fichiers")]
    [TestCase("{0,letters} fichiers")]
    [TestCase("{0} fichiers {")]
    public async Task ApplyCached_MalformedPlaceholderFallsBackWithoutLosingValidTranslations(string malformed)
    {
        var remote = new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch"), ("Home.Status", "{0} files")),
            ["Strings.fr.resx"] = Resx(("Home.Launch", "Lancer"), ("Home.Status", malformed)),
        };
        var builtIn = new FakeStringSource()
            .Add("en", "Home.Launch", "Launch").Add("en", "Home.Status", "{0} files")
            .Add("fr", "Home.Launch", "Demarrer").Add("fr", "Home.Status", "Nombre de fichiers : {0}");
        var (service, _, files, _) = Create(remote, inBuild: builtIn);
        Assert.That(await service.DownloadAsync(), Is.True);

        var (restarted, localization, _, _) = Create(remote, inBuild: builtIn, files: files);
        restarted.ApplyCached();
        localization.SetLanguage("fr");

        Assert.That(localization["Home.Launch"], Is.EqualTo("Lancer"));
        Assert.That(localization.Format("Home.Status", 3), Is.EqualTo("Nombre de fichiers : 3"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Download_IncompleteRefreshKeepsMatchingTranslationsUntilRetry(bool sourceChanges)
    {
        var remote = new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch"), ("Home.Status", "{0} files")),
            ["Strings.fr.resx"] = Resx(("Home.Launch", "Lancer"), ("Home.Status", "{0} fichiers")),
        };
        var (previousLauncher, _, files, _) = Create(remote);
        Assert.That(await previousLauncher.DownloadAsync(), Is.True);

        const string englishFile = "Strings.resx";
        const string frenchFile = "Strings.fr.resx";
        string newEnglish = Resx(("Home.Launch", "Launch"), ("Home.Status", "{0} folders"));
        string newFrench = Resx(("Home.Launch", "Lancer Redux"), ("Home.Status", "{0} dossiers"));
        remote[englishFile] = newEnglish;
        remote[frenchFile] = newFrench;
        var upgradedBuild = new FakeStringSource()
            .Add("en", "Home.Launch", "Launch").Add("en", "Home.Status", "{0} folders")
            .Add("fr", "Home.Launch", "Demarrer").Add("fr", "Home.Status", "Nombre de dossiers : {0}");
        var (interrupted, beforeRefresh, _, requests) = Create(remote, request =>
        {
            if (request.RequestUri!.Segments[^1] != frenchFile)
                return Serve(remote, request);
            if (!sourceChanges)
                throw new HttpRequestException("Connection lost after receiving the new English text");
            remote[englishFile] = Resx(("Home.Launch", "Launch"), ("Home.Status", "{0} archives"));
            remote[frenchFile] = Resx(("Home.Launch", "Lancer Redux"), ("Home.Status", "{0} archives"));
            return Serve(remote, request);
        }, upgradedBuild, files);
        interrupted.ApplyCached();
        beforeRefresh.SetLanguage("fr");
        Assert.That(beforeRefresh["Home.Launch"], Is.EqualTo("Lancer"));
        Assert.That(beforeRefresh.Format("Home.Status", 3), Is.EqualTo("Nombre de dossiers : 3"));

        await interrupted.DownloadAsync();
        Assert.That(requests[0].RequestUri!.Segments[^1], Is.EqualTo(englishFile));
        Assert.That(requests.Any(request => request.RequestUri!.Segments[^1] == frenchFile), Is.True);

        var (restarted, localization, _, _) = Create(remote, inBuild: upgradedBuild, files: files);
        restarted.ApplyCached();
        localization.SetLanguage("fr");
        Assert.That(localization["Home.Launch"], Is.EqualTo("Lancer"));
        Assert.That(localization.Format("Home.Status", 3), Is.EqualTo("Nombre de dossiers : 3"));

        remote[englishFile] = newEnglish;
        remote[frenchFile] = newFrench;
        Assert.That(await restarted.DownloadAsync(), Is.True);
        restarted.ApplyCached();
        Assert.That(localization["Home.Launch"], Is.EqualTo("Lancer Redux"));
        Assert.That(localization.Format("Home.Status", 3), Is.EqualTo("3 dossiers"));
    }

    [Test]
    public async Task Download_NewEnglishRefreshesTheLocaleWithoutItsOldValidator()
    {
        const string englishFile = "Strings.resx";
        const string frenchFile = "Strings.fr.resx";
        var remote = new Dictionary<string, string>
        {
            [englishFile] = Resx(("Home.Status", "{0} files")),
            [frenchFile] = Resx(("Home.Status", "{0} fichiers")),
        };
        var (oldLauncher, _, files, _) = Create(remote);
        Assert.That(await oldLauncher.DownloadAsync(), Is.True);

        remote[englishFile] = Resx(("Home.Status", "{0} folders"));
        remote[frenchFile] = Resx(("Home.Status", "{0} dossiers"));
        var upgradedBuild = new FakeStringSource()
            .Add("en", "Home.Status", "{0} folders").Add("fr", "Home.Status", "Nombre de dossiers : {0}");
        var (upgraded, localization, _, _) = Create(remote, request =>
        {
            if (request.RequestUri!.Segments[^1] == frenchFile && request.Headers.IfNoneMatch.Count > 0)
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            return Serve(remote, request);
        }, upgradedBuild, files);

        Assert.That(await upgraded.DownloadAsync(), Is.True);
        upgraded.ApplyCached();
        localization.SetLanguage("fr");
        Assert.That(localization.Format("Home.Status", 3), Is.EqualTo("3 dossiers"));
    }

    [Test]
    public async Task ApplyCached_WhenTurnedOff_UsesOnlyTheBuildsTexts()
    {
        var (service, localization, _, _) = Create(new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch")),
            ["Strings.de.resx"] = Resx(("Home.Launch", "Jetzt starten")),
        }, inBuild: new FakeStringSource().Add("en", "Home.Launch", "Launch").Add("de", "Home.Launch", "Starten"));
        await service.DownloadAsync();
        service.ApplyCached();
        localization.SetLanguage("de");
        Assert.That(localization["Home.Launch"], Is.EqualTo("Jetzt starten"));

        _config.DownloadTranslationUpdates = false;
        service.ApplyCached();

        Assert.That(localization["Home.Launch"], Is.EqualTo("Starten"));
    }

    [Test]
    public async Task Download_Offline_KeepsTheCacheAndStopsAfterTheFirstFailure()
    {
        var remote = new Dictionary<string, string>
        {
            ["Strings.resx"] = Resx(("Home.Launch", "Launch")),
            ["Strings.fr.resx"] = Resx(("Home.Launch", "Lancer")),
        };
        var (service, _, files, _) = Create(remote);
        Assert.That(await service.DownloadAsync(), Is.True);
        var calls = 0;
        var (offline, localization, _, _) = Create(remote, _ =>
        {
            calls++;
            throw new HttpRequestException("No route to host");
        }, files: files);

        Assert.That(await offline.DownloadAsync(), Is.False);
        Assert.That(calls, Is.EqualTo(1), "The other files would only wait out the same timeout");
        offline.ApplyCached();
        localization.SetLanguage("fr");
        Assert.That(localization["Home.Launch"], Is.EqualTo("Lancer"));
    }

    private LauncherConfig _config = null!;

    private (TranslationUpdateService Service, LocalizationService Localization, MockFileSystem Files, List<HttpRequestMessage> Requests)
        Create(Dictionary<string, string> remote, Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
            FakeStringSource? inBuild = null, MockFileSystem? files = null)
    {
        _config = new LauncherConfig { TranslationsUrl = BASE_URL };
        var config = new Mock<ILauncherConfigService>();
        config.Setup(c => c.Config).Returns(_config);
        config.Setup(c => c.GetLocalStorageDirectory()).Returns(STORAGE);

        var source = inBuild ?? new FakeStringSource().Add("en", "Home.Launch", "Launch").Add("en", "Home.Status", "{0} files");
        var localization = new LocalizationService(source, CultureInfo.GetCultureInfo("en-US"));
        files ??= new MockFileSystem();
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request);
            return respond?.Invoke(request) ?? Serve(remote, request);
        });

        var service = new TranslationUpdateService(localization, source, config.Object, files, Mock.Of<ILogService>(), handler);
        return (service, localization, files, requests);
    }

    // Serves the files with an ETag made from their content, answering 304 when the client already has it.
    private static HttpResponseMessage Serve(Dictionary<string, string> remote, HttpRequestMessage request)
    {
        var name = request.RequestUri!.Segments[^1];
        if (!remote.TryGetValue(name, out var body)) return new HttpResponseMessage(HttpStatusCode.NotFound);

        var etag = new EntityTagHeaderValue($"\"{(uint)body.GetHashCode()}\"");
        if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == etag.Tag)) return new HttpResponseMessage(HttpStatusCode.NotModified);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        response.Headers.ETag = etag;
        return response;
    }

    private static string Resx(params (string Key, string Value)[] entries) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><root>" +
        string.Concat(entries.Select(entry => $"<data name=\"{entry.Key}\" xml:space=\"preserve\"><value>{entry.Value}</value></data>")) +
        "</root>";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
