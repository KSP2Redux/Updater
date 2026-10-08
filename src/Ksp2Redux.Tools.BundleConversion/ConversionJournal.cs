using System.IO.Abstractions;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// The record of every change the converter made to an install, used to verify and revert it.
/// </summary>
// The game reads this file to check the edits at startup, so the property names and shapes are a
// contract. Add fields at the end and bump Version rather than renaming anything.
public sealed class ConversionJournal
{
    /// <summary>
    /// The journal format version this build writes.
    /// </summary>
    public const int CURRENT_VERSION = 1;

    /// <summary>
    /// Gets or sets the journal format version.
    /// </summary>
    public int Version { get; set; } = CURRENT_VERSION;

    /// <summary>
    /// Gets or sets the lowercase hex SHA-256 of the shader manifest the conversion used.
    /// </summary>
    public string ShaderManifestSha256 { get; set; } = "";

    /// <summary>
    /// Gets or sets the stock bundle folder, relative to the game folder, with forward slashes.
    /// </summary>
    public string BundleRoot { get; set; } = BundleConversionLayout.BUNDLE_ROOT;

    /// <summary>
    /// Gets or sets the stock bundles edited in place.
    /// </summary>
    public List<JournalInPlaceFile> InPlace { get; set; } = [];

    /// <summary>
    /// Gets or sets the bundles written as converted copies under the Redux folder.
    /// </summary>
    public List<JournalRewrittenFile> Rewritten { get; set; } = [];

    /// <summary>
    /// Gets or sets the shader references that were left alone, one entry per bundle, shader and reason.
    /// </summary>
    public List<JournalSkipped> Skipped { get; set; } = [];
}

/// <summary>
/// A stock bundle edited in place.
/// </summary>
public sealed class JournalInPlaceFile
{
    /// <summary>
    /// Gets or sets the bundle file name inside the bundle root.
    /// </summary>
    public string File { get; set; } = "";

    /// <summary>
    /// Gets or sets the bundle size in bytes, which the edits never change.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the byte edits.
    /// </summary>
    public List<JournalEdit> Edits { get; set; } = [];
}

/// <summary>
/// One same-size byte edit in a stock bundle.
/// </summary>
public sealed class JournalEdit
{
    /// <summary>
    /// Gets or sets the absolute offset of the edit in the bundle file.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Gets or sets the stock bytes, as lowercase hex.
    /// </summary>
    public string Old { get; set; } = "";

    /// <summary>
    /// Gets or sets the converted bytes, as lowercase hex.
    /// </summary>
    public string New { get; set; } = "";
}

/// <summary>
/// A bundle written as a converted copy.
/// </summary>
public sealed class JournalRewrittenFile
{
    /// <summary>
    /// Gets or sets the bundle file name inside the bundle root.
    /// </summary>
    public string File { get; set; } = "";

    /// <summary>
    /// Gets or sets the size of the stock bundle the copy was made from.
    /// </summary>
    public long StockSize { get; set; }

    /// <summary>
    /// Gets or sets the lowercase hex SHA-256 of the stock bundle the copy was made from.
    /// </summary>
    public string StockSha256 { get; set; } = "";

    /// <summary>
    /// Gets or sets the converted copy, relative to the game folder, with forward slashes.
    /// </summary>
    public string Output { get; set; } = "";

    /// <summary>
    /// Gets or sets the lowercase hex SHA-256 of the converted copy, or empty while it is being written.
    /// </summary>
    public string OutputSha256 { get; set; } = "";
}

/// <summary>
/// A shader reference the conversion left alone.
/// </summary>
public sealed class JournalSkipped
{
    /// <summary>
    /// Gets or sets the bundle file name holding the reference.
    /// </summary>
    public string File { get; set; } = "";

    /// <summary>
    /// Gets or sets the shader name, or a CAB and path ID when the name could not be read.
    /// </summary>
    public string Shader { get; set; } = "";

    /// <summary>
    /// Gets or sets why the reference was left alone.
    /// </summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// Reads and atomically writes the conversion journal.
/// </summary>
public sealed class JournalStore
{
    // The relaxed encoder writes shader names such as "<CAB-x:123>" as they are instead of as <
    // escapes. The journal is a data file, never embedded in HTML, so the stricter default buys nothing.
    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IFileSystem _fileSystem;
    private readonly string _path;

    /// <summary>
    /// Initializes a store over a journal path.
    /// </summary>
    /// <param name="fileSystem">The file system to read and write through.</param>
    /// <param name="path">The journal file path.</param>
    public JournalStore(IFileSystem fileSystem, string path)
    {
        _fileSystem = fileSystem;
        _path = path;
    }

    /// <summary>
    /// Gets the journal file path.
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// Gets a value indicating whether a journal exists.
    /// </summary>
    public bool Exists => _fileSystem.File.Exists(_path);

    /// <summary>
    /// Reads the journal.
    /// </summary>
    /// <returns>The journal, or null when there is none.</returns>
    /// <exception cref="InvalidDataException">The journal exists but cannot be read.</exception>
    // A journal that cannot be read is an error rather than an empty journal, because treating it
    // as empty would forget edits that only it knows how to revert.
    public ConversionJournal? Read()
    {
        if (!_fileSystem.File.Exists(_path))
        {
            return null;
        }

        ConversionJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<ConversionJournal>(_fileSystem.File.ReadAllBytes(_path), JSON_OPTIONS);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The conversion journal {_path} is not valid JSON: {e.Message}", e);
        }

        if (journal is null)
        {
            throw new InvalidDataException($"The conversion journal {_path} is empty.");
        }

        if (journal.Version != ConversionJournal.CURRENT_VERSION)
        {
            throw new InvalidDataException(
                $"The conversion journal {_path} has version {journal.Version}, this build reads version {ConversionJournal.CURRENT_VERSION}.");
        }

        return journal;
    }

    /// <summary>
    /// Writes the journal so that a reader sees either the old file or the complete new one.
    /// </summary>
    /// <param name="journal">The journal to write.</param>
    public void Write(ConversionJournal journal)
    {
        var directory = _fileSystem.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        _fileSystem.File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(journal, JSON_OPTIONS));
        AtomicFile.Replace(_fileSystem, temporary, _path);
    }

    /// <summary>
    /// Deletes the journal, and its temporary file if a write was interrupted.
    /// </summary>
    public void Delete()
    {
        _fileSystem.File.Delete(_path);
        _fileSystem.File.Delete(_path + ".tmp");
    }
}

/// <summary>
/// Moves a finished temporary file over its destination.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Replaces <paramref name="destination" /> with <paramref name="temporary" />.
    /// </summary>
    /// <param name="fileSystem">The file system to act through.</param>
    /// <param name="temporary">The complete new file.</param>
    /// <param name="destination">The file to replace, which may not exist yet.</param>
    // File.Replace swaps the files in one step on NTFS and is a rename elsewhere. It needs the
    // destination to exist, so a first write is a plain move.
    public static void Replace(IFileSystem fileSystem, string temporary, string destination)
    {
        if (fileSystem.File.Exists(destination))
        {
            fileSystem.File.Replace(temporary, destination, null);
        }
        else
        {
            fileSystem.File.Move(temporary, destination);
        }
    }
}
