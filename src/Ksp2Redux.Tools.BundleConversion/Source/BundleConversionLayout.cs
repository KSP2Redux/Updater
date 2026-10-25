using System.IO.Abstractions;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Where the stock bundles, the converted copies and the journal live: inside a KSP2 install, or with the
/// copies and the journal in a cache folder of their own.
/// </summary>
public sealed class BundleConversionLayout
{
    /// <summary>
    /// The stock bundle folder relative to the game folder, as the journal records it.
    /// </summary>
    public const string BUNDLE_ROOT = "KSP2_x64_Data/StreamingAssets/aa/StandaloneWindows64";

    /// <summary>
    /// The folder holding the journal and the converted copies, relative to the game folder.
    /// </summary>
    public const string CONVERSION_ROOT = "Redux/BundleConversion";

    /// <summary>
    /// The converted copy folder relative to the game folder, as the journal records it.
    /// </summary>
    public const string OUTPUT_ROOT = CONVERSION_ROOT + "/StandaloneWindows64";

    /// <summary>
    /// The folder of overlay data files relative to the game folder, as the journal records it.
    /// </summary>
    public const string OVERLAY_ROOT = CONVERSION_ROOT + "/Overlays";

    /// <summary>
    /// The journal file name inside the conversion folder.
    /// </summary>
    public const string JOURNAL_FILE = "journal.json";

    private const string OVERLAY_EXTENSION = ".overlay";

    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes the layout for a game folder.
    /// </summary>
    /// <param name="fileSystem">The file system the paths are built with.</param>
    /// <param name="gameDirectory">The KSP2 folder, the one holding KSP2_x64.exe.</param>
    /// <param name="cacheDirectory">A folder that holds the journal and the copies instead of the game folder, or null.</param>
    public BundleConversionLayout(IFileSystem fileSystem, string gameDirectory, string? cacheDirectory = null)
    {
        _fileSystem = fileSystem;
        GameDirectory = fileSystem.Path.GetFullPath(gameDirectory);
        ConversionBase = cacheDirectory is null ? GameDirectory : fileSystem.Path.GetFullPath(cacheDirectory);
        BundleDirectory = Combine(GameDirectory, BUNDLE_ROOT);
        ConversionDirectory = Resolve(CONVERSION_ROOT);
        OutputDirectory = Resolve(OUTPUT_ROOT);
        OverlayDirectory = Resolve(OVERLAY_ROOT);
        JournalPath = fileSystem.Path.Combine(ConversionDirectory, JOURNAL_FILE);
        InstallJournalPath = fileSystem.Path.Combine(Combine(GameDirectory, CONVERSION_ROOT), JOURNAL_FILE);
    }

    /// <summary>
    /// Gets the absolute game folder.
    /// </summary>
    public string GameDirectory { get; }

    /// <summary>
    /// Gets the absolute folder the journal's relative paths start from: the game folder, or the cache folder.
    /// </summary>
    public string ConversionBase { get; }

    /// <summary>
    /// Gets a value indicating whether the journal and the copies live in a cache folder rather than the install.
    /// </summary>
    public bool IsCache => !string.Equals(ConversionBase, GameDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the absolute path of the install's own journal, which a cache conversion reads but never writes.
    /// </summary>
    public string InstallJournalPath { get; }

    /// <summary>
    /// Gets the absolute stock bundle folder.
    /// </summary>
    public string BundleDirectory { get; }

    /// <summary>
    /// Gets the absolute folder holding the journal and the converted copies.
    /// </summary>
    public string ConversionDirectory { get; }

    /// <summary>
    /// Gets the absolute converted copy folder.
    /// </summary>
    public string OutputDirectory { get; }

    /// <summary>
    /// Gets the absolute folder of overlay data files.
    /// </summary>
    public string OverlayDirectory { get; }

    /// <summary>
    /// Gets the absolute journal path.
    /// </summary>
    public string JournalPath { get; }

    /// <summary>
    /// Gets the absolute path of a stock bundle.
    /// </summary>
    /// <param name="file">The bundle file name.</param>
    /// <returns>The path inside the stock bundle folder.</returns>
    public string BundlePath(string file) => _fileSystem.Path.Combine(BundleDirectory, file);

    /// <summary>
    /// Gets the path of a converted copy relative to the game folder, as the journal records it.
    /// </summary>
    /// <param name="file">The bundle file name.</param>
    /// <returns>The relative path with forward slashes.</returns>
    public static string RelativeOutput(string file) => OUTPUT_ROOT + "/" + file;

    /// <summary>
    /// Gets the path of a bundle's overlay data file relative to the game folder, as the journal records it.
    /// </summary>
    /// <param name="file">The bundle file name.</param>
    /// <returns>The relative path with forward slashes.</returns>
    public static string RelativeOverlay(string file) => OVERLAY_ROOT + "/" + file + OVERLAY_EXTENSION;

    /// <summary>
    /// Turns a path the journal recorded relative to the game folder, or to the cache folder, into an absolute one.
    /// </summary>
    /// <param name="relative">A path with forward slashes, relative to <see cref="ConversionBase" />.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="InvalidDataException">The path leaves that folder.</exception>
    // The journal is a file on disk that anything could have edited, so a path in it is checked
    // before revert deletes whatever it names.
    public string Resolve(string relative) => Combine(ConversionBase, relative);

    private string Combine(string root, string relative)
    {
        var path = root;
        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "." || segment == "..")
            {
                throw new InvalidDataException($"'{relative}' is not a plain path inside {root}.");
            }

            path = _fileSystem.Path.Combine(path, segment);
        }

        return path;
    }
}
