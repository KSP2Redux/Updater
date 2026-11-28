using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourceInstallationService
{
    private const string RELEASED_STOCK_HEAD_SUFFIX = "-released-stock.json";

    /// <inheritdoc />
    public async Task<FullSourceInstallationStatus> UninstallToStockAsync(Guid installationId, Action<string> log,
        CancellationToken ct, string expectedInstallRoot)
    {
        EnsureSupported();
        Ksp2InstallEntry entry = Entry(installationId, expectedInstallRoot);
        using FileSystemStream lease = Lock(entry);
        EnsureMutationAllowed(entry);
        Catalog catalog = EnsureReadyLocked(entry) ?? throw new InvalidDataException("The installation has no full-source catalog.");
        if (catalog.Release is not null || catalog.LastResult != InstallTransactionResult.Committed)
        {
            await UninstallLockedAsync(entry, log, ct);
            catalog = ReadPublished(entry);
        }

        return FinishStockHandoffLocked(entry, catalog, log, ct);
    }

    private FullSourceInstallationStatus? ResumeStockHandoffLocked(Ksp2InstallEntry entry)
    {
        Catalog? catalog = ReadCatalog(entry);
        if (catalog is not { Pending: null, Release: null, LastResult: InstallTransactionResult.Committed, Archive: { } archive } ||
            Exists(archive.Path) || !Exists(ReleasedStockArchivePath(entry, catalog.Stock)))
            return null;
        if (Exists(_fs.Path.Combine(Root(entry), ".redux-transaction.active")))
            throw new IOException("An active transaction prevents stock handoff recovery. Evidence retained.");

        EnsureMutationAllowed(entry);
        return FinishStockHandoffLocked(entry, catalog, message => _log.Info(message), CancellationToken.None);
    }

    private FullSourceInstallationStatus FinishStockHandoffLocked(Ksp2InstallEntry entry, Catalog catalog,
        Action<string> log, CancellationToken ct)
    {
        StockBaselineFile archive = RequireCompletedStockRestoration(entry, catalog);
        string root = Root(entry);
        if (Exists(At(root, StockBaselineManifest.MANIFEST_PATH)))
            throw new InvalidDataException("The game folder contains unexpected full-source backup metadata. All files were preserved.");
        VerifyTree(root, Manifest(catalog.Stock), ct);

        string retainedArchive = ReleasedStockArchivePath(entry, catalog.Stock);
        string retiredHead = ReleasedStockHeadPath(entry, catalog.Stock);
        _engine.ValidateControlledPath(retainedArchive);
        _engine.ValidateControlledPath(retiredHead);
        Catalog? previousRelease = ReadReleasedStockHead(entry, catalog.Stock);
        bool archiveInGame = Exists(archive.Path);
        if (archiveInGame == Exists(retainedArchive))
            throw new IOException("Stock handoff found missing or conflicting backup evidence. All files were preserved.");

        var configured = _configuration.Config.Ksp2Installs.Single(candidate => candidate.Id == entry.Id);
        var settings = catalog.StockBootConfigRestore;
        if (settings is { Count: > 0 } || (_bootConfig is not null && settings is not null) ||
            configured.BootConfigOriginals.Count != 0 || configured.BootConfigReleaseSettings is not null)
        {
            if (!TryRestoreStockBootConfigLocked(entry, catalog, settings ?? new Dictionary<string, string>()))
                throw new IOException("Stock boot settings could not be restored. Retry Uninstall Redux to finish.");
            catalog = catalog with { StockBootConfigRestore = null };
        }
        if (!_configuration.TrySave())
            throw new IOException("Stock installation settings could not be saved. Retry Uninstall Redux to finish.");

        if (archiveInGame && _os.IsWindows() && RetireCompletedPayloadsLocked(entry, ct))
            catalog = catalog with { RetentionPolicy = RETENTION };

        EnsureMutationAllowed(entry);
        ct.ThrowIfCancellationRequested();
        string head = HeadPath(entry);
        if (archiveInGame)
        {
            StockArchive.CreateDirectory(_fs, StorageRoot(entry));
            Boundary("release-stock-backup", () =>
            {
                RequirePublishedBytes(head, catalog);
                RequireBackupPin(archive, ct);
                StockArchive.PublishFile(_fs, archive.Path, retainedArchive, overwrite: false);
            });
        }

        // The published head keeps legacy writes blocked until the verified archive is out of the game folder.
        // A retry finds the moved archive at the path for this exact stock contract.
        Boundary("release-stock-catalog", () =>
        {
            RequireBackupPin(archive with { Path = retainedArchive }, ct);
            _engine.ValidateControlledPath(head);
            RequirePublishedBytes(head, catalog);
            ct.ThrowIfCancellationRequested();
            if (Exists(archive.Path))
                throw new IOException("The live backup path was recreated during stock handoff. All files were preserved.");
            if (previousRelease is not null)
                RequirePublishedBytes(retiredHead, previousRelease);
            StockArchive.PublishFile(_fs, head, retiredHead, overwrite: previousRelease is not null);
        });
        log("Stock KSP2 is ready. An older Redux build can now be installed normally.");
        return Status(catalog, null);
    }

    private StockBaselineFile RequireCompletedStockRestoration(Ksp2InstallEntry entry, Catalog catalog)
    {
        if (catalog.Pending is not null || catalog.Release is not null || catalog.LastResult != InstallTransactionResult.Committed ||
            catalog.LastTransaction is not { } id || id == Guid.Empty || catalog.Archive is not { } archive)
            throw new InvalidDataException("The stock restoration did not finish. Recovery evidence was preserved.");

        Operation restoration = Read<Operation>(OperationPath(entry, id));
        if (restoration.FormatVersion != FORMAT_VERSION || restoration.TransactionId != id ||
            restoration.InstallationId != entry.Id || restoration.InstallRoot != catalog.InstallRoot ||
            restoration.Phase != "terminal" || restoration.Result != InstallTransactionResult.Committed ||
            restoration.Release is not null || restoration.Archive != archive || restoration.Target is null ||
            !NormalizeContract(restoration.Target).SequenceEqual(catalog.Stock))
            throw new InvalidDataException("The published stock generation does not match its completed restoration.");
        return archive;
    }

    private string ReleasedStockArchivePath(Ksp2InstallEntry entry, byte[] stock) =>
        _fs.Path.Combine(StorageRoot(entry), $"{ReleasedStockKey(entry, stock)}-released-stock.zip");

    private string ReleasedStockHeadPath(Ksp2InstallEntry entry, byte[] stock) =>
        _fs.Path.Combine(CatalogRoot(entry), $"{ReleasedStockKey(entry, stock)}{RELEASED_STOCK_HEAD_SUFFIX}");

    private string ReleasedStockKey(Ksp2InstallEntry entry, byte[] stock)
    {
        string root = Root(entry);
        string keyRoot = _os.IsWindows() ? root.ToUpperInvariant() : root;
        return Digest(Encoding.UTF8.GetBytes($"{keyRoot}\0{Digest(stock)}"));
    }

    private Catalog? ReadReleasedStockHead(Ksp2InstallEntry entry, byte[] stock)
    {
        string path = ReleasedStockHeadPath(entry, stock);
        if (!Exists(path))
            return null;

        Catalog previous = Read<Catalog>(path);
        ValidateReleasedStockHead(entry, stock, previous);
        return previous;
    }

    private void ValidateReleasedStockHead(Ksp2InstallEntry entry, byte[] stock, Catalog previous)
    {
        string root = Root(entry);
        StringComparison pathComparison = _os.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (previous.FormatVersion != FORMAT_VERSION || previous.InstallationId != entry.Id ||
            !string.Equals(previous.InstallRoot, root, pathComparison) ||
            !string.Equals(previous.StorageRoot, StorageRoot(entry), pathComparison) ||
            previous.Stock is null || !previous.Stock.AsSpan().SequenceEqual(stock) ||
            previous.UnownedDirectories is null || previous.StockBootConfigRestore is { Count: > 0 })
            throw new InvalidDataException("Released stock metadata does not match this installation. Evidence retained.");
        ValidatePaths(previous.UnownedDirectories, true);
        StockBaselineFile archive = RequireCompletedStockRestoration(entry, previous);
        // Check the historical pin against its recorded root without opening a stale spelling.
        if (archive.Path != _fs.Path.Combine(previous.InstallRoot, "uninstall.zip"))
            throw new InvalidDataException("Released stock archive reference does not match its recorded root. Evidence retained.");
        ValidateArchiveReference(entry, archive with { Path = _fs.Path.Combine(root, "uninstall.zip") });
    }

    private bool HasRetainedReduxDirectory(Ksp2InstallEntry entry)
    {
        static bool IsReduxDirectory(string path) => path.Equals("Redux", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Redux/", StringComparison.OrdinalIgnoreCase);

        string root = Root(entry);
        Ksp2InstallEntry configured = _configuration.Config.Ksp2Installs.Single(candidate => candidate.Id == entry.Id);
        ValidateExpectedRoot(Root(configured), root);
        if (configured.FullSourceUnownedDirectories.Any(IsReduxDirectory))
            return true;

        // Legacy can snapshot settings left by a previous stock handoff. Do not treat those bytes as debris.
        StringComparison pathComparison = _os.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string path in _fs.Directory.EnumerateFileSystemEntries(CatalogRoot(entry), $"*{RELEASED_STOCK_HEAD_SUFFIX}"))
        {
            Catalog previous = Read<Catalog>(path);
            if (!string.Equals(previous.InstallRoot, root, pathComparison))
                continue;
            if (previous.Stock is not { } stock || !string.Equals(path, ReleasedStockHeadPath(entry, stock), pathComparison))
                throw new InvalidDataException("Released stock metadata conflicts with its recorded path. Evidence retained.");
            ValidateReleasedStockHead(entry, stock, previous);
            if (previous.UnownedDirectories.Any(IsReduxDirectory))
                return true;
        }
        return false;
    }

    private string[] GetReleasedStockUnownedDirectories(Ksp2InstallEntry entry, FullSourceStockPreparation stock)
    {
        string[] configured = stock.UnownedDirectories.ToArray();
        Catalog? previous = ReadReleasedStockHead(entry, stock.Stock.ToJson());
        if (previous is null)
            return configured;

        string root = Root(entry);
        string[] directories = previous.UnownedDirectories.Where(path => _fs.Directory.Exists(At(root, path)))
            .Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        ValidatePaths(directories, true);
        return directories;
    }

    private StockBaselineFile? TryReuseReleasedStockArchive(Ksp2InstallEntry entry, byte[] stock, CancellationToken ct)
    {
        string retainedPath = ReleasedStockArchivePath(entry, stock);
        if (!Exists(retainedPath))
            return null;

        Catalog previous = ReadReleasedStockHead(entry, stock)
            ?? throw new InvalidDataException("The retained stock archive has no matching release metadata. Evidence retained.");
        StockBaselineFile archive = previous.Archive! with { Path = _fs.Path.Combine(Root(entry), "uninstall.zip") };
        Boundary("reuse-released-stock-backup", () =>
        {
            RequireBackupPin(archive with { Path = retainedPath }, ct);
            RequirePublishedBytes(ReleasedStockHeadPath(entry, stock), previous);
            _engine.ValidateControlledPath(archive.Path);
            ct.ThrowIfCancellationRequested();
            // The pending catalog can verify and adopt the live archive if interrupted after the move.
            StockArchive.PublishFile(_fs, retainedPath, archive.Path, overwrite: false);
        });
        return archive;
    }
}
