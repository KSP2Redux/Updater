using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.Restoration;

/// <summary>
/// A versioned expected stock tree supplied independently of the bytes being archived.
/// </summary>
/// <remarks>
/// Identity and provenance describe the caller's source. They do not authenticate it.
/// Storefront or release authentication belongs above the archive primitive.
/// </remarks>
public class StockBaselineManifest
{
    /// <summary>
    /// The supported manifest and archive format version.
    /// </summary>
    public const int FORMAT_VERSION = 1;

    /// <summary>
    /// The contract version carrying explicit Unix permission metadata.
    /// </summary>
    public const int UNIX_FORMAT_VERSION = 2;

    /// <summary>
    /// Gets whether every file and directory has an explicit Unix permission contract.
    /// </summary>
    public bool HasUnixMetadata { get; }

    /// <summary>
    /// Gets the explicit Unix permission bits for all relative directories.
    /// </summary>
    public IReadOnlyDictionary<string, int> UnixDirectoryModes { get; }

    /// <summary>
    /// The reserved archive metadata entry. Stock paths cannot occupy its root directory.
    /// </summary>
    public const string MANIFEST_PATH = ".redux-stock/manifest.json";

    /// <summary>
    /// Gets the caller-supplied storefront, build and depot identity.
    /// </summary>
    public string BaselineIdentity { get; }

    /// <summary>
    /// Gets the description of how the caller obtained the expected contract.
    /// </summary>
    public string Provenance { get; }

    /// <summary>
    /// Gets the normalized files in ordinal path order.
    /// </summary>
    public IReadOnlyList<StockBaselineFile> Files { get; }

    /// <summary>
    /// Gets all directories, including inferred parents and explicit empty directories.
    /// </summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>
    /// Gets the expected total uncompressed file bytes.
    /// </summary>
    public long TotalBytes { get; }

    /// <summary>
    /// Construct and validate an expected baseline without reading an installed tree.
    /// </summary>
    /// <param name="baselineIdentity">The caller-supplied baseline identity.</param>
    /// <param name="provenance">The source description, without implied authentication.</param>
    /// <param name="files">The independently supplied expected files and hashes.</param>
    /// <param name="directories">The explicit directories, including empty directories.</param>
    /// <param name="unixDirectoryModes">The complete directory permission map, or null for version one.</param>
    public StockBaselineManifest(string baselineIdentity, string provenance,
        IEnumerable<StockBaselineFile> files, IEnumerable<string>? directories = null,
        IReadOnlyDictionary<string, int>? unixDirectoryModes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        ArgumentNullException.ThrowIfNull(files);
        if (baselineIdentity.Length > 4096 || provenance.Length > 4096)
            throw new InvalidDataException("Baseline identity or provenance is too long.");

        BaselineIdentity = baselineIdentity;
        Provenance = provenance;
        var paths = new Dictionary<string, (string Path, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
        var explicitDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedFiles = new List<StockBaselineFile>();
        foreach (string directory in directories ?? [])
        {
            string path = NormalizePath(directory);
            if (!explicitDirectories.Add(path))
                throw new InvalidDataException($"Duplicate directory '{path}'.");
            AddPath(paths, path, true);
        }
        foreach (StockBaselineFile file in files)
        {
            if (file is null)
                throw new InvalidDataException("A baseline file is null.");
            string path = NormalizePath(file.Path);
            if (file.Length < 0 || file.Sha256 is null || file.Sha256.Length != 64 ||
                file.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"Invalid length or SHA-256 for '{path}'.");
            AddPath(paths, path, false);
            if (file.UnixMode is { } mode && (mode < 0 || mode > 511 || (mode & 256) == 0))
                throw new InvalidDataException($"Unsupported Unix file permissions for '{path}'.");
            normalizedFiles.Add(new StockBaselineFile(path, file.Length, file.Sha256.ToUpperInvariant(), file.UnixMode));
            TotalBytes = checked(TotalBytes + file.Length);
        }
        Files = normalizedFiles.OrderBy(file => file.Path, StringComparer.Ordinal).ToList().AsReadOnly();
        Directories = paths.Values.Where(path => path.IsDirectory).Select(path => path.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToList().AsReadOnly();
        HasUnixMetadata = unixDirectoryModes is not null || Files.Any(file => file.UnixMode is not null);
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (HasUnixMetadata)
        {
            if (Files.Any(file => file.UnixMode is null) || unixDirectoryModes is null ||
                unixDirectoryModes.Count != Directories.Count)
                throw new InvalidDataException("Unix metadata must cover every file and directory.");
            foreach (string directory in Directories)
            {
                if (!unixDirectoryModes.TryGetValue(directory, out int mode) || mode < 0 || mode > 511 || (mode & 448) != 448)
                    throw new InvalidDataException($"Unsupported or absent Unix directory permissions for '{directory}'.");
                modes.Add(directory, mode);
            }
        }
        UnixDirectoryModes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(modes);
    }

    /// <summary>
    /// Serialize the self-contained contract in the canonical version-one JSON layout.
    /// </summary>
    /// <param name="maxBytes">The maximum serialized metadata bytes.</param>
    /// <returns>The UTF-8 manifest bytes.</returns>
    public byte[] ToJson(int maxBytes = 16 * 1024 * 1024)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", HasUnixMetadata ? UNIX_FORMAT_VERSION : FORMAT_VERSION);
            writer.WriteString("hashAlgorithm", "SHA-256");
            writer.WriteString("baselineIdentity", BaselineIdentity);
            writer.WriteString("provenance", Provenance);
            writer.WriteStartArray("files");
            foreach (StockBaselineFile file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("sha256", file.Sha256);
                if (HasUnixMetadata)
                    writer.WriteNumber("unixMode", file.UnixMode!.Value);
                writer.WriteEndObject();
                CheckJsonBudget(writer, maxBytes);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("directories");
            foreach (string directory in Directories)
            {
                writer.WriteStringValue(directory);
                CheckJsonBudget(writer, maxBytes);
            }
            writer.WriteEndArray();
            if (HasUnixMetadata)
            {
                writer.WriteStartObject("unixDirectoryModes");
                foreach (string directory in Directories)
                {
                    writer.WriteNumber(directory, UnixDirectoryModes[directory]);
                    CheckJsonBudget(writer, maxBytes);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            CheckJsonBudget(writer, maxBytes);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Read an untrusted manifest and validate its version, shape and Windows paths.
    /// </summary>
    /// <remarks>
    /// Parsing does not authenticate provenance or promote an installed-tree scan to stock.
    /// </remarks>
    /// <param name="json">The UTF-8 manifest bytes.</param>
    /// <param name="limits">The resource limits.</param>
    /// <returns>The validated expected-tree description.</returns>
    public static StockBaselineManifest FromJson(ReadOnlyMemory<byte> json, StockArchiveLimits? limits = null)
    {
        limits ??= new StockArchiveLimits();
        if (json.Length > limits.MaxManifestBytes)
            throw new InvalidDataException("Manifest exceeds the metadata budget.");
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        int version = root.GetProperty("formatVersion").GetInt32();
        if (version == UNIX_FORMAT_VERSION)
            CheckProperties(root, "formatVersion", "hashAlgorithm", "baselineIdentity", "provenance", "files", "directories", "unixDirectoryModes");
        else
            CheckProperties(root, "formatVersion", "hashAlgorithm", "baselineIdentity", "provenance", "files", "directories");
        if (version is not (FORMAT_VERSION or UNIX_FORMAT_VERSION) ||
            root.GetProperty("hashAlgorithm").GetString() != "SHA-256")
            throw new InvalidDataException("Unsupported stock manifest format.");
        JsonElement filesJson = root.GetProperty("files");
        JsonElement directoriesJson = root.GetProperty("directories");
        if ((long)filesJson.GetArrayLength() + directoriesJson.GetArrayLength() + 1 > limits.MaxEntries)
            throw new InvalidDataException("Manifest exceeds the entry budget.");
        var files = new List<StockBaselineFile>();
        foreach (JsonElement file in filesJson.EnumerateArray())
        {
            if (version == UNIX_FORMAT_VERSION)
                CheckProperties(file, "path", "length", "sha256", "unixMode");
            else
                CheckProperties(file, "path", "length", "sha256");
            files.Add(new StockBaselineFile(file.GetProperty("path").GetString()!,
                file.GetProperty("length").GetInt64(), file.GetProperty("sha256").GetString()!,
                version == UNIX_FORMAT_VERSION ? file.GetProperty("unixMode").GetInt32() : null));
        }
        Dictionary<string, int>? directoryModes = null;
        if (version == UNIX_FORMAT_VERSION)
        {
            directoryModes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in root.GetProperty("unixDirectoryModes").EnumerateObject())
            {
                if (!directoryModes.TryAdd(property.Name, property.Value.GetInt32()))
                    throw new InvalidDataException("Unix directory metadata repeats a path.");
            }
        }
        var manifest = new StockBaselineManifest(root.GetProperty("baselineIdentity").GetString()!,
            root.GetProperty("provenance").GetString()!, files,
            directoriesJson.EnumerateArray().Select(directory => directory.GetString()!), directoryModes);
        limits.Validate(manifest);
        return manifest;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new InvalidDataException("An empty stock path is invalid.");
        string normalized = path.Replace('\\', '/');
        if (normalized.Length > 32760 || System.Text.Encoding.UTF8.GetByteCount(normalized) > 65534)
            throw new InvalidDataException("Stock path is too long.");
        foreach (string segment in normalized.Split('/'))
        {
            ValidateSegment(segment);
        }
        if (normalized.Split('/')[0].Equals(".redux-stock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stock path conflicts with the archive metadata namespace.");
        return normalized;
    }

    internal static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > 255 || segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
            throw new InvalidDataException($"Invalid Windows path segment '{segment}'.");
        for (int index = 0; index < segment.Length; index++)
        {
            if (!char.IsSurrogate(segment[index]))
                continue;
            if (!char.IsHighSurrogate(segment[index]) || index + 1 == segment.Length ||
                !char.IsLowSurrogate(segment[++index]))
                throw new InvalidDataException("Stock path contains an unpaired Unicode surrogate.");
        }
        string device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
            (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) ||
                                   device.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789\u00b9\u00b2\u00b3".Contains(device[3])))
            throw new InvalidDataException($"Windows device path '{segment}' is invalid.");
    }

    private static void AddPath(Dictionary<string, (string Path, bool IsDirectory)> paths, string path, bool isDirectory)
    {
        while (true)
        {
            if (paths.TryGetValue(path, out var existing))
            {
                if (!isDirectory || !existing.IsDirectory || existing.Path != path)
                    throw new InvalidDataException($"Duplicate, case-colliding or conflicting stock path '{path}'.");
            }
            else
            {
                paths.Add(path, (path, isDirectory));
            }
            int slash = path.LastIndexOf('/');
            if (slash < 0)
                return;
            path = path[..slash];
            isDirectory = true;
        }
    }

    private static void CheckJsonBudget(Utf8JsonWriter writer, int maxBytes)
    {
        if (writer.BytesCommitted + writer.BytesPending > maxBytes)
            throw new InvalidDataException("Manifest exceeds the metadata budget.");
    }

    private static void CheckProperties(JsonElement element, params string[] names)
    {
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                throw new InvalidDataException($"Unexpected or repeated manifest property '{property.Name}'.");
        }
        if (remaining.Count != 0)
            throw new InvalidDataException("Manifest properties are missing.");
    }
}

/// <summary>
/// An expected stock file persisted in the manifest.
/// </summary>
/// <param name="Path">The normalized relative path.</param>
/// <param name="Length">The expected byte length.</param>
/// <param name="Sha256">The expected SHA-256 digest in hexadecimal.</param>
/// <param name="UnixMode">The explicit Unix permission bits, or null for version one.</param>
public record StockBaselineFile(string Path, long Length, string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UnixMode = null);
