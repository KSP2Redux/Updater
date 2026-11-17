using System.Globalization;
using System.IO.Abstractions;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Threading;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Localization;

/// <summary>
/// Brings in translations finished after this launcher was released. The translation pipeline commits the
/// Strings.*.resx files to the launcher repository every night, and players would otherwise only see them
/// with the next launcher version.
/// </summary>
public interface ITranslationUpdateService
{
    /// <summary>
    /// Hands the translations downloaded on an earlier start to the localization service. Reads only the local
    /// cache, so it is fast enough to run before the main window opens.
    /// </summary>
    void ApplyCached();

    /// <summary>
    /// Downloads the files that changed since the last start. Returns true when the cache changed. Network
    /// problems are logged, never thrown, since the translations in the build still work.
    /// </summary>
    Task<bool> DownloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads, then applies the cache on the UI thread when anything changed.</summary>
    Task UpdateAsync(CancellationToken cancellationToken = default);
}

public sealed partial class TranslationUpdateService : ITranslationUpdateService
{
    private const string NEUTRAL_FILE = "Strings.resx";
    private const string ETAGS_FILE = "etags.json";
    private const string CACHE_FOLDER = "translations";

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoTexts =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    private readonly ILocalizationService _localization;
    private readonly IStringSource _builtIn;
    private readonly ILauncherConfigService _config;
    private readonly IFileSystem _fileSystem;
    private readonly ILogService _log;
    private readonly HttpClient _http;

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <param name="builtIn">The texts in the build. Its English decides which downloaded texts still fit.</param>
    /// <param name="handler">Replaces the network in tests.</param>
    public TranslationUpdateService(ILocalizationService localization, IStringSource builtIn, ILauncherConfigService config,
        IFileSystem fileSystem, ILogService log, HttpMessageHandler? handler = null)
    {
        _localization = localization;
        _builtIn = builtIn;
        _config = config;
        _fileSystem = fileSystem;
        _log = log;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ksp2Redux-Launcher");
    }

    private string CacheDirectory => _fileSystem.Path.Combine(_config.GetLocalStorageDirectory(), CACHE_FOLDER);

    private static string FileName(string culture) => $"Strings.{culture}.resx";

    private static IEnumerable<string> FileNames() =>
        LocalizationService.TranslationCultures.Select(FileName).Prepend(NEUTRAL_FILE);

    public void ApplyCached()
    {
        if (!_config.Config.DownloadTranslationUpdates || ReadCached(NEUTRAL_FILE) is not { } remoteEnglish)
        {
            _localization.SetDownloadedTexts(NoTexts);
            return;
        }

        var texts = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var culture in LocalizationService.TranslationCultures)
        {
            if (ReadCached(FileName(culture)) is not { } translation) continue;
            var usable = SelectUsable(remoteEnglish, translation, key => _builtIn.GetString(key, EnglishCulture));
            if (usable.Count > 0) texts[culture] = usable;
        }

        if (texts.Count > 0)
        {
            _log.Info($"Using downloaded translations for {string.Join(", ", texts.Select(pair => $"{pair.Key} ({pair.Value.Count})"))}.");
        }

        _localization.SetDownloadedTexts(texts);
    }

    public async Task<bool> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.Config.DownloadTranslationUpdates) return false;

        var etags = ReadEtags();
        var changed = false;
        foreach (var file in FileNames())
        {
            try
            {
                changed |= await DownloadFileAsync(file, etags, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or UnauthorizedAccessException
                                           || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Every file comes from the same host, so after one failure the rest would only wait out the
                // same timeout. The cache from the last start stays in use.
                _log.Warn($"Couldn't download translation updates ({file}): {ex.Message}");
                break;
            }
        }

        WriteEtags(etags);
        return changed;
    }

    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!await DownloadAsync(cancellationToken)) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyCached();
            // A language that only exists as a download can now be the configured or the system one.
            _localization.SetLanguage(_config.Config.Language);
        });
    }

    private async Task<bool> DownloadFileAsync(string file, Dictionary<string, string> etags, CancellationToken cancellationToken)
    {
        var path = _fileSystem.Path.Combine(CacheDirectory, file);
        var cached = _fileSystem.File.Exists(path);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.Config.TranslationsUrl.TrimEnd('/')}/{file}");
        if (cached && etags.TryGetValue(file, out var etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return false;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The pipeline removes a language that fell below its threshold, so the launcher drops it too.
            etags.Remove(file);
            if (!cached) return false;
            _fileSystem.File.Delete(path);
            return true;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        ReadResx(bytes);
        if (response.Headers.ETag is { } newTag) etags[file] = newTag.ToString();
        else etags.Remove(file);
        if (cached && _fileSystem.File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;

        _fileSystem.Directory.CreateDirectory(CacheDirectory);
        await _fileSystem.File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return true;
    }

    /// <summary>
    /// The translated texts this launcher can use: the key exists in the build with the same English text the
    /// translation was made for, the text is not blank, and it has the same placeholders as the English one.
    /// A key whose English changed after this release is left to the build's own translation.
    /// </summary>
    internal static Dictionary<string, string> SelectUsable(IReadOnlyDictionary<string, string> remoteEnglish,
        IReadOnlyDictionary<string, string> translation, Func<string, string?> builtInEnglish)
    {
        var usable = new Dictionary<string, string>();
        foreach (var (key, text) in translation)
        {
            if (string.IsNullOrWhiteSpace(text) || !remoteEnglish.TryGetValue(key, out var english)) continue;
            if (builtInEnglish(key) != english) continue;
            if (!PlaceholderIndices(english).SequenceEqual(PlaceholderIndices(text))) continue;
            usable[key] = text;
        }

        return usable;
    }

    private static int[] PlaceholderIndices(string text) =>
        Placeholder().Matches(text.Replace("{{", "").Replace("}}", ""))
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();

    internal static Dictionary<string, string> ReadResx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var root = XDocument.Load(stream).Root ?? throw new XmlException("The file has no root element.");
        var texts = new Dictionary<string, string>();
        foreach (var data in root.Elements("data"))
        {
            // Binary resources carry a type or mime type. Text entries have neither.
            if (data.Attribute("type") is not null || data.Attribute("mimetype") is not null) continue;
            if (data.Attribute("name")?.Value is not { } name) continue;
            texts[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return texts;
    }

    private Dictionary<string, string>? ReadCached(string file)
    {
        var path = _fileSystem.Path.Combine(CacheDirectory, file);
        try
        {
            return _fileSystem.File.Exists(path) ? ReadResx(_fileSystem.File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            _log.Warn($"Ignoring the cached translation file {file}: {ex.Message}");
            return null;
        }
    }

    private Dictionary<string, string> ReadEtags()
    {
        var path = _fileSystem.Path.Combine(CacheDirectory, ETAGS_FILE);
        try
        {
            return _fileSystem.File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(_fileSystem.File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void WriteEtags(Dictionary<string, string> etags)
    {
        try
        {
            _fileSystem.Directory.CreateDirectory(CacheDirectory);
            _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(CacheDirectory, ETAGS_FILE), JsonSerializer.Serialize(etags));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't save the translation download state: {ex.Message}");
        }
    }
}
