using System.IO.Abstractions;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourceInstallationService
{
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

        if (catalog.Release is not null || catalog.LastResult != InstallTransactionResult.Committed ||
            catalog.LastTransaction is not { } id || catalog.Archive is not { } archive)
            throw new InvalidDataException("The stock restoration did not finish. Recovery evidence was preserved.");

        Operation restoration = Read<Operation>(OperationPath(entry, id));
        if (restoration.FormatVersion != FORMAT_VERSION || restoration.TransactionId != id ||
            restoration.InstallationId != entry.Id || restoration.InstallRoot != Root(entry) ||
            restoration.Phase != "terminal" || restoration.Result != InstallTransactionResult.Committed ||
            restoration.Release is not null || restoration.Archive != archive || restoration.Target is null ||
            !NormalizeContract(restoration.Target).SequenceEqual(catalog.Stock))
            throw new InvalidDataException("The published stock generation does not match its completed restoration.");

        string root = Root(entry);
        if (Exists(At(root, StockBaselineManifest.MANIFEST_PATH)))
            throw new InvalidDataException("The game folder contains unexpected full-source backup metadata. All files were preserved.");
        VerifyTree(root, Manifest(catalog.Stock), ct);

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

        string storage = StorageRoot(entry);
        string retainedArchive = _fs.Path.Combine(storage, $"{id:N}-released-stock.zip");
        string retiredHead = _fs.Path.Combine(CatalogRoot(entry), $"{id:N}-released.json");
        _engine.ValidateControlledPath(retainedArchive);
        _engine.ValidateControlledPath(retiredHead);
        bool archiveInGame = Exists(archive.Path);
        if (archiveInGame == Exists(retainedArchive) || Exists(retiredHead))
            throw new IOException("Stock handoff found missing or conflicting backup evidence. All files were preserved.");

        if (archiveInGame && _os.IsWindows() && RetireCompletedPayloadsLocked(entry, ct))
            catalog = catalog with { RetentionPolicy = RETENTION };

        EnsureMutationAllowed(entry);
        ct.ThrowIfCancellationRequested();
        string head = HeadPath(entry);
        if (archiveInGame)
        {
            StockArchive.CreateDirectory(_fs, storage);
            Boundary("release-stock-backup", () =>
            {
                RequirePublishedBytes(head, catalog);
                RequireBackupPin(archive, ct);
                StockArchive.PublishFile(_fs, archive.Path, retainedArchive, overwrite: false);
            });
        }

        // The published head keeps legacy writes blocked until the verified archive is out of the game folder.
        // A retry can find the moved archive using the committed restoration ID and the same pin.
        Boundary("release-stock-catalog", () =>
        {
            RequireBackupPin(archive with { Path = retainedArchive }, ct);
            _engine.ValidateControlledPath(head);
            RequirePublishedBytes(head, catalog);
            ct.ThrowIfCancellationRequested();
            if (Exists(archive.Path))
                throw new IOException("The live backup path was recreated during stock handoff. All files were preserved.");
            StockArchive.PublishFile(_fs, head, retiredHead, overwrite: false);
        });
        log("Stock KSP2 is ready. An older Redux build can now be installed normally.");
        return Status(catalog, null);
    }
}
