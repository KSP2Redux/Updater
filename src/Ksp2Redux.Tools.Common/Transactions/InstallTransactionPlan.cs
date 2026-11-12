using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Common.Transactions;

/// <summary>
/// An independently supplied, versioned ownership contract for one installation generation change.
/// </summary>
/// <remarks>
/// Recovery requires the same trusted contract. A disk journal never supplies authority or paths.
/// Current directories are owned. Existing unowned directories and exact unowned files stay unowned.
/// </remarks>
public class InstallTransactionPlan
{
    private readonly Dictionary<string, StockBaselineFile> _currentFiles;
    private readonly Dictionary<string, StockBaselineFile> _targetFiles;
    private readonly byte[] _canonicalBytes;
    private readonly string _digest;

    /// <summary>
    /// The supported canonical plan and journal version.
    /// </summary>
    public const int FORMAT_VERSION = 1;

    /// <summary>
    /// The plan version binding changed-file staging while preserving version-one recovery.
    /// </summary>
    public const int CHANGED_FILE_STAGING_VERSION = 2;

    /// <summary>
    /// The plan version binding direct stock-archive target staging.
    /// </summary>
    public const int STOCK_ARCHIVE_STAGING_VERSION = 3;

    /// <summary>
    /// The stock restoration plan version using the original renamed file as undo for removals.
    /// </summary>
    public const int MOVED_FILE_UNDO_VERSION = 4;

    /// <summary>
    /// The Windows directory-source plan version using the original renamed file as undo for removals.
    /// </summary>
    public const int DIRECTORY_SOURCE_MOVED_UNDO_VERSION = 5;

    /// <summary>
    /// The Windows plan version that moves new files from disposable source storage into native staging.
    /// </summary>
    public const int OWNED_SOURCE_STAGING_VERSION = 6;

    /// <summary>
    /// Gets whether target and undo storage exclude unchanged and retained unowned files.
    /// </summary>
    public bool UsesChangedFileStaging { get; }

    /// <summary>
    /// Gets whether changed stock files are read from the pinned archive during staging.
    /// </summary>
    public bool UsesStockArchiveSource { get; }

    /// <summary>
    /// Gets whether removed files serve as their own undo instead of requiring a separate copy.
    /// </summary>
    public bool UsesMovedFileUndo { get; }

    /// <summary>
    /// Gets whether new target files can be consumed from this operation's private source directory.
    /// </summary>
    public bool UsesOwnedSourceStaging { get; }

    /// <summary>
    /// Gets the unique transaction identity.
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// Gets the absolute installation root.
    /// </summary>
    public string InstallRoot { get; }

    /// <summary>
    /// Gets the stable target source root, requiring only changed owned outputs for version two.
    /// </summary>
    public string SourceRoot { get; }

    /// <summary>
    /// Gets the new transaction directory on the installation volume.
    /// </summary>
    public string TransactionRoot { get; }

    /// <summary>
    /// Gets the independently expected current managed generation.
    /// </summary>
    public StockBaselineManifest Current { get; }

    /// <summary>
    /// Gets the independently expected complete target generation.
    /// </summary>
    public StockBaselineManifest Target { get; }

    /// <summary>
    /// Gets the complete original stock contract verified before creating this plan.
    /// </summary>
    public StockBaselineManifest Stock { get; }

    /// <summary>
    /// Gets the immutable archive reference verified against the stock contract by StockArchive.
    /// </summary>
    public StockBaselineFile Archive { get; }

    /// <summary>
    /// Gets target file paths explicitly required to preexist with exact target bytes and remain unowned.
    /// </summary>
    public IReadOnlyList<string> RetainedUnownedFiles { get; }

    /// <summary>
    /// Gets current or target directory paths explicitly required to preexist and remain unowned.
    /// </summary>
    public IReadOnlyList<string> ExistingUnownedDirectories { get; }

    /// <summary>
    /// Gets additional roots that neither active mutations nor transaction storage may overlap.
    /// </summary>
    public IReadOnlyList<string> ProtectedRoots { get; }

    /// <summary>
    /// Construct a bounded plan from independently authenticated caller contracts.
    /// </summary>
    /// <param name="transactionId">The nonempty unique identity.</param>
    /// <param name="installRoot">The absolute installation path.</param>
    /// <param name="sourceRoot">The absolute stable target source path.</param>
    /// <param name="transactionRoot">The absent absolute transaction directory.</param>
    /// <param name="current">The expected current managed files and owned directories.</param>
    /// <param name="target">The expected target file set and directories.</param>
    /// <param name="stock">The independently verified original stock contract.</param>
    /// <param name="archive">The absolute archive path, byte length and verified SHA-256.</param>
    /// <param name="retainedUnownedFiles">The exact target files retained without acquiring ownership.</param>
    /// <param name="existingUnownedDirectories">The existing current or target directories retained without ownership.</param>
    /// <param name="protectedRoots">The additional protected source, archive and user installation roots.</param>
    /// <param name="useChangedFileStaging">True for version-two changed-file storage, false for legacy complete staging.</param>
    /// <param name="useStockArchiveSource">True when staging the exact original stock target directly from its archive.</param>
    /// <param name="useMovedFileUndo">True when retaining the original file by rename for removal rollback.</param>
    /// <param name="useOwnedSourceStaging">True when the private Windows source is disposable and on the transaction volume.</param>
    public InstallTransactionPlan(Guid transactionId, string installRoot, string sourceRoot,
        string transactionRoot, StockBaselineManifest current, StockBaselineManifest target,
        StockBaselineManifest stock, StockBaselineFile archive,
        IEnumerable<string>? retainedUnownedFiles = null,
        IEnumerable<string>? existingUnownedDirectories = null, IEnumerable<string>? protectedRoots = null,
        bool useChangedFileStaging = false, bool useStockArchiveSource = false, bool useMovedFileUndo = false,
        bool useOwnedSourceStaging = false)
    {
        if (transactionId == Guid.Empty)
            throw new InvalidDataException("A transaction requires a unique identity.");
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(archive);
        foreach (string root in new[] { installRoot, sourceRoot, transactionRoot, archive.Path })
        {
            if (string.IsNullOrWhiteSpace(root) || root.Length > 32760)
                throw new InvalidDataException("Invalid transaction disk path length.");
        }
        TransactionId = transactionId;
        UsesChangedFileStaging = useChangedFileStaging;
        UsesStockArchiveSource = useStockArchiveSource;
        UsesMovedFileUndo = useMovedFileUndo;
        UsesOwnedSourceStaging = useOwnedSourceStaging;
        if (useStockArchiveSource && (!useChangedFileStaging || stock.HasUnixMetadata ||
            !target.ToJson().AsSpan().SequenceEqual(stock.ToJson())))
            throw new InvalidDataException("Archive-backed staging requires the exact Windows stock target and changed-file storage.");
        if (useMovedFileUndo && (!useChangedFileStaging || stock.HasUnixMetadata))
            throw new InvalidDataException("Moved-file undo requires Windows changed-file staging.");
        if (useOwnedSourceStaging && (!useMovedFileUndo || useStockArchiveSource))
            throw new InvalidDataException("Owned source staging requires a Windows directory source with moved-file undo.");
        InstallRoot = installRoot;
        SourceRoot = sourceRoot;
        TransactionRoot = transactionRoot;
        Current = current;
        Target = target;
        Stock = stock;
        _currentFiles = current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        _targetFiles = target.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (archive.Length is <= 0 or > 130L * 1024 * 1024 * 1024 || archive.Sha256 is null ||
            archive.Sha256.Length != 64 || !archive.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid archive reference.");
        Archive = archive with { Sha256 = archive.Sha256.ToUpperInvariant() };
        RetainedUnownedFiles = Paths(retainedUnownedFiles);
        ExistingUnownedDirectories = Paths(existingUnownedDirectories);
        string[] protectedPaths = (protectedRoots ?? []).Take(1025).ToArray();
        if (protectedPaths.Length > 1024 || protectedPaths.Any(path => path is null || path.Length > 32760))
            throw new InvalidDataException("Protected roots exceed the path budget.");
        ProtectedRoots = protectedPaths.Order(StringComparer.Ordinal).ToList().AsReadOnly();
        var limits = new StockArchiveLimits();
        limits.Validate(current);
        limits.Validate(target);
        limits.Validate(stock);
        foreach (string path in current.Files.Select(file => file.Path).Concat(current.Directories)
                     .Concat(target.Files.Select(file => file.Path)).Concat(target.Directories))
        {
            if (path.Split('/')[0].Equals("uninstall.zip", StringComparison.OrdinalIgnoreCase) ||
                path.Split('/')[0].StartsWith(".redux-transaction", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Managed paths conflict with transaction or archive metadata.");
        }
        foreach (string path in RetainedUnownedFiles)
        {
            if (!_targetFiles.ContainsKey(path) || _currentFiles.ContainsKey(path))
                throw new InvalidDataException("Retained unowned files must be target-only paths.");
        }
        foreach (string path in ExistingUnownedDirectories)
        {
            if (!target.Directories.Contains(path, StringComparer.Ordinal) &&
                !current.Directories.Contains(path, StringComparer.Ordinal))
                throw new InvalidDataException("Unowned directories must belong to a current or target tree.");
        }
        // Combined validation rejects case changes and file/directory transitions before any disk access.
        // Only paths are combined. The original manifests retain their complete permission contracts.
        var union = new StockBaselineManifest("transaction-union", "path conflict validation",
            current.Files.Concat(target.Files).GroupBy(file => file.Path, StringComparer.Ordinal)
                .Select(group => group.First() with { UnixMode = null }), current.Directories.Concat(target.Directories).Distinct(StringComparer.Ordinal));
        limits.Validate(union);
        _canonicalBytes = Serialize();
        _digest = Convert.ToHexString(SHA256.HashData(_canonicalBytes));
    }

    /// <summary>
    /// Serialize the canonical bounded plan used to bind disk intent to caller authority.
    /// </summary>
    /// <returns>The canonical UTF-8 bytes.</returns>
    public byte[] ToJson() => (byte[])_canonicalBytes.Clone();

    /// <summary>
    /// Find the trusted current file contract by its normalized ordinal path.
    /// </summary>
    /// <param name="path">The normalized manifest path.</param>
    /// <returns>The current file contract, or null when absent.</returns>
    internal StockBaselineFile? CurrentFile(string path) => _currentFiles.GetValueOrDefault(path);

    /// <summary>
    /// Find the trusted target file contract by its normalized ordinal path.
    /// </summary>
    /// <param name="path">The normalized manifest path.</param>
    /// <returns>The target file contract, or null when absent.</returns>
    internal StockBaselineFile? TargetFile(string path) => _targetFiles.GetValueOrDefault(path);

    /// <summary>
    /// Determine whether the plan requires a staged target blob for a contracted file.
    /// </summary>
    /// <param name="file">The complete target file contract.</param>
    /// <returns>True if target storage is required, false otherwise.</returns>
    internal bool RequiresTargetBlob(StockBaselineFile file) => !UsesChangedFileStaging ||
        (CurrentFile(file.Path) != file && !RetainedUnownedFiles.Contains(file.Path));

    /// <summary>
    /// Determines whether a new file is moved into the work directory instead of copied.
    /// </summary>
    /// <param name="file">The complete target file contract.</param>
    /// <returns>True if the new file consumes its owned source copy.</returns>
    internal bool MovesSourceFile(StockBaselineFile file) => UsesOwnedSourceStaging &&
        TargetFile(file.Path) == file && CurrentFile(file.Path) is null && !RetainedUnownedFiles.Contains(file.Path);

    /// <summary>
    /// Determine whether the plan requires original undo bytes for a contracted file.
    /// </summary>
    /// <param name="file">The complete current file contract.</param>
    /// <returns>True if original undo storage is required, false otherwise.</returns>
    internal bool RequiresUndoBlob(StockBaselineFile file) =>
        !UsesChangedFileStaging || (TargetFile(file.Path) != file && !IsMovedUndo(file));

    /// <summary>
    /// Determines whether the original itself provides rollback bytes for this removal.
    /// </summary>
    /// <param name="file">The complete current file contract.</param>
    /// <returns>True if removal uses the original file as undo.</returns>
    internal bool IsMovedUndo(StockBaselineFile file) => UsesMovedFileUndo && TargetFile(file.Path) is null;

    private byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            int version = (UsesOwnedSourceStaging, UsesMovedFileUndo, UsesStockArchiveSource, UsesChangedFileStaging) switch
            {
                (true, _, _, _) => OWNED_SOURCE_STAGING_VERSION,
                (false, true, true, _) => MOVED_FILE_UNDO_VERSION,
                (false, true, false, _) => DIRECTORY_SOURCE_MOVED_UNDO_VERSION,
                (false, false, true, _) => STOCK_ARCHIVE_STAGING_VERSION,
                (false, false, false, true) => CHANGED_FILE_STAGING_VERSION,
                _ => FORMAT_VERSION
            };
            writer.WriteNumber("formatVersion", version);
            writer.WriteString("transactionId", TransactionId);
            writer.WriteString("installRoot", InstallRoot);
            writer.WriteString("sourceRoot", SourceRoot);
            writer.WriteString("transactionRoot", TransactionRoot);
            writer.WritePropertyName("current");
            writer.WriteRawValue(Current.ToJson());
            writer.WritePropertyName("target");
            writer.WriteRawValue(Target.ToJson());
            writer.WritePropertyName("stock");
            writer.WriteRawValue(Stock.ToJson());
            writer.WriteString("archivePath", Archive.Path);
            writer.WriteNumber("archiveLength", Archive.Length);
            writer.WriteString("archiveSha256", Archive.Sha256);
            WritePaths(writer, "retainedUnownedFiles", RetainedUnownedFiles);
            WritePaths(writer, "existingUnownedDirectories", ExistingUnownedDirectories);
            WritePaths(writer, "protectedRoots", ProtectedRoots);
            writer.WriteEndObject();
        }
        if (stream.Length > 48 * 1024 * 1024)
            throw new InvalidDataException("Transaction plan exceeds 48 MiB.");
        return stream.ToArray();
    }

    /// <summary>
    /// Get the SHA-256 identity of the canonical complete plan.
    /// </summary>
    /// <returns>The uppercase hexadecimal digest.</returns>
    public string GetDigest() => _digest;

    private static IReadOnlyList<string> Paths(IEnumerable<string>? paths)
    {
        string[] result = (paths ?? []).Take(100_001).Select(StockBaselineManifest.NormalizePath)
            .Order(StringComparer.Ordinal).ToArray();
        if (result.Length > 100_000 || result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new InvalidDataException("Duplicate paths or ownership budget exceeded.");
        return Array.AsReadOnly(result);
    }

    private static void WritePaths(Utf8JsonWriter writer, string name, IEnumerable<string> paths)
    {
        writer.WriteStartArray(name);
        foreach (string path in paths)
        {
            writer.WriteStringValue(path);
            if (writer.BytesCommitted + writer.BytesPending > 48 * 1024 * 1024)
                throw new InvalidDataException("Transaction plan exceeds 48 MiB.");
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// The durable result of applying or recovering a generation.
/// </summary>
public enum InstallTransactionResult
{
    /// <summary>
    /// All target bytes were verified before the committed journal record was flushed.
    /// </summary>
    Committed,
    /// <summary>
    /// The independently expected original managed generation was restored and verified.
    /// </summary>
    RolledBack
}
