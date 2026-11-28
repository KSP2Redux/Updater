using System.Globalization;
using System.IO.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    private const string SNAPSHOT_FILE = "snapshot.json";
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
    private readonly SemaphoreSlim _refresh = new(1);

    private sealed record CachedFile(string? ETag, Dictionary<string, string> Texts);

    private sealed record CacheSnapshot(string SourceKey, Dictionary<string, CachedFile> Files);

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
    private string CachePath => _fileSystem.Path.Combine(CacheDirectory, SNAPSHOT_FILE);
    private string? SourceUrl => _config.Config.TranslationsUrl?.TrimEnd('/');

    private static string FileName(string culture) => $"Strings.{culture}.resx";

    public void ApplyCached()
    {
        if (!_config.Config.DownloadTranslationUpdates || SourceUrl is not { Length: > 0 } source)
        {
            _localization.SetDownloadedTexts(NoTexts);
            return;
        }

        var files = ReadSnapshot(source);
        if (files.GetValueOrDefault(NEUTRAL_FILE)?.Texts is not { } remoteEnglish)
        {
            _localization.SetDownloadedTexts(NoTexts);
            return;
        }

        var texts = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var culture in LocalizationService.TranslationCultures)
        {
            if (files.GetValueOrDefault(FileName(culture))?.Texts is not { } translation) continue;
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
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (!_config.Config.DownloadTranslationUpdates || SourceUrl is not { Length: > 0 } source)
                return false;

            var current = ReadSnapshot(source);
            var previousEnglish = CachedTranslation(current, NEUTRAL_FILE);
            var english = await DownloadFileAsync(source, NEUTRAL_FILE, previousEnglish, cancellationToken, revalidate: true);
            if (english is null)
                return false;

            var files = new Dictionary<string, CachedFile> { [NEUTRAL_FILE] = english };
            bool changed = !ReferenceEquals(previousEnglish, english);
            bool englishChanged = previousEnglish is null || !SameTexts(previousEnglish, english);
            foreach (string culture in LocalizationService.TranslationCultures)
            {
                string name = FileName(culture);
                var cached = CachedTranslation(current, name);
                // A validator from the old English snapshot cannot vouch for a translation of the new English.
                var downloaded = await DownloadFileAsync(source, name, englishChanged ? null : cached, cancellationToken,
                    revalidate: englishChanged);
                if (downloaded is not null)
                    files.Add(name, downloaded);

                changed |= !ReferenceEquals(cached, downloaded);
            }

            if (!changed)
                return false;

            // The branch can advance during the download. Only publish files checked against the same English.
            var confirmed = await DownloadFileAsync(source, NEUTRAL_FILE, english, cancellationToken, revalidate: true);
            if (confirmed is null || !SameTexts(english, confirmed))
                return false;
            files[NEUTRAL_FILE] = confirmed;

            await SaveSnapshotAsync(new CacheSnapshot(SourceKey(source), files), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or UnauthorizedAccessException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _log.Warn($"Couldn't download translation updates: {ex.Message}");
            return false;
        }
        finally
        {
            _refresh.Release();
        }
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

    private async Task<CachedFile?> DownloadFileAsync(string source, string name, CachedFile? cached,
        CancellationToken cancellationToken, bool revalidate)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{source}/{name}");
        if (revalidate)
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (cached?.ETag is { } etag)
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return cached ?? throw new HttpRequestException($"Received an unchanged response for uncached translation {name}.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var downloaded = new CachedFile(response.Headers.ETag?.ToString(), ReadResx(bytes));
        return cached is not null && cached.ETag == downloaded.ETag && SameTexts(cached, downloaded)
            ? cached
            : downloaded;
    }

    private static CachedFile? CachedTranslation(Dictionary<string, CachedFile> files, string name) =>
        files.GetValueOrDefault(name) is { Texts: not null } file ? file : null;

    private static bool SameTexts(CachedFile first, CachedFile second) =>
        first.Texts.Count == second.Texts.Count
        && first.Texts.All(pair => second.Texts.TryGetValue(pair.Key, out var text) && text == pair.Value);

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
            if (string.IsNullOrWhiteSpace(text) || !remoteEnglish.TryGetValue(key, out var english) || english is null) continue;
            if (builtInEnglish(key) != english) continue;
            if (!TryGetPlaceholderIndices(english, out var expected) || !TryGetPlaceholderIndices(text, out var translated)
                || !expected.SetEquals(translated) || (expected.Count > 0 && !IsCompositeFormat(text)))
                continue;
            usable[key] = text;
        }

        return usable;
    }

    private static bool IsCompositeFormat(string text)
    {
        try
        {
            _ = CompositeFormat.Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetPlaceholderIndices(string text, out HashSet<int> indices)
    {
        indices = [];
        foreach (Match match in Placeholder().Matches(text.Replace("{{", "").Replace("}}", "")))
        {
            if (!int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                return false;
            indices.Add(index);
        }
        return true;
    }

    internal static Dictionary<string, string> ReadResx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var root = XDocument.Load(stream).Root;
        if (root is null || root.Name != "root")
            throw new XmlException("The file is not a RESX resource.");
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

    private Dictionary<string, CachedFile> ReadSnapshot(string source)
    {
        try
        {
            if (!_fileSystem.File.Exists(CachePath))
                return [];
            var snapshot = JsonSerializer.Deserialize<CacheSnapshot>(_fileSystem.File.ReadAllText(CachePath));
            return snapshot is { Files: not null } && snapshot.SourceKey == SourceKey(source) ? snapshot.Files : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn($"Ignoring the cached translations: {ex.Message}");
            return [];
        }
    }

    private static string SourceKey(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private async Task SaveSnapshotAsync(CacheSnapshot snapshot, CancellationToken cancellationToken)
    {
        _fileSystem.Directory.CreateDirectory(CacheDirectory);
        string temporary = _fileSystem.Path.Combine(CacheDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await _fileSystem.File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(snapshot), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.File.Move(temporary, CachePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (_fileSystem.File.Exists(temporary))
                    _fileSystem.File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Couldn't remove an unfinished translation snapshot: {ex.Message}");
            }
        }
    }
}
