using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Launcher-owned installation authority, durable operation coordination and ownership publication.
/// </summary>
/// <remarks>
/// Catalog integrity is local evidence, not release authentication. Native mutation requires Windows.
/// Published Windows transactions retire verified duplicate payloads and retain catalogs, journals and the full stock archive.
/// Mutation checks the configured game's process and installation file use under the coordinator lease.
/// External writers and launches after a file-use check still require exclusion by the user.
/// </remarks>
public partial class FullSourceInstallationService : IFullSourceInstallationService
{
    private const string BOOT_CONFIG = "KSP2_x64_Data/boot.config";
    private const int OBSERVATION_BUFFER_BYTES = 1024 * 1024;
    private readonly IFileSystem _fs;
    private readonly ILauncherConfigService _configuration;
    private readonly IFullSourcePreparationService _preparation;
    private readonly IOperatingSystemService _os;
    private readonly ILogService _log;
    private readonly IGameProcessService _gameProcesses;
    private readonly IInstallFileLockService _fileLocks;
    private readonly StockArchive _archive;
    private readonly InstallTransactionEngine _engine;
    private readonly Action<string, bool>? _boundary;

    /// <summary>
    /// Construct the shared coordinator using normal launcher filesystem, configuration and logging services.
    /// </summary>
    /// <param name="fileSystem">The filesystem abstraction.</param>
    /// <param name="configuration">The configured installation identities and controlled local storage.</param>
    /// <param name="zipFileService">The canonical stock archive ZIP service.</param>
    /// <param name="diskSpace">The host free-space query.</param>
    /// <param name="preparation">The independent release and stock authentication boundary.</param>
    /// <param name="operatingSystem">The host platform service.</param>
    /// <param name="log">The launcher log.</param>
    /// <param name="gameProcesses">The existing game process lifecycle service.</param>
    /// <param name="fileLocks">The existing installation file-use safeguard.</param>
    /// <param name="operationBoundary">The optional interruption observer for standalone verification.</param>
    /// <param name="engine">The optional transaction engine for filesystem fault verification.</param>
    public FullSourceInstallationService(IFileSystem fileSystem, ILauncherConfigService configuration,
        IZipFileService zipFileService, IDiskSpaceService diskSpace, IFullSourcePreparationService preparation,
        IOperatingSystemService operatingSystem, ILogService log, IGameProcessService gameProcesses,
        IInstallFileLockService fileLocks, Action<string, bool>? operationBoundary = null,
        InstallTransactionEngine? engine = null)
    {
        _fs = fileSystem;
        _configuration = configuration;
        _preparation = preparation;
        _os = operatingSystem;
        _log = log;
        _gameProcesses = gameProcesses;
        _fileLocks = fileLocks;
        _boundary = operationBoundary;
        _archive = new StockArchive(fileSystem, zipFileService, availableSpace: diskSpace.GetAvailableFreeSpace,
            compressEntries: operatingSystem.IsWindows());
        _engine = engine ?? new InstallTransactionEngine(fileSystem, diskSpace.GetAvailableFreeSpace);
    }

    /// <inheritdoc />
    public bool HasCatalog(string root)
    {
        // Keep the legacy host path independent of the Windows-only adapter when no catalog exists.
        Ksp2InstallEntry? configured = _configuration.Config.Ksp2Installs.FirstOrDefault(entry =>
            _fs.Path.GetDirectoryName(entry.ExePath)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true);
        if (configured is null || !Exists(_fs.Path.Combine(_configuration.GetLocalStorageDirectory(), "full-source-v1",
                configured.Id.ToString("N"), "installation.json")))
            return false;
        EnsureSupported();
        Ksp2InstallEntry? entry = EntryAt(root);
        return entry is not null && Exists(HeadPath(entry));
    }

    /// <inheritdoc />
    public FullSourceReleaseIdentity? GetPublishedIdentity(Guid installationId)
    {
        Ksp2InstallEntry entry = Entry(installationId);
        using FileSystemStream lease = Lock(entry);
        Catalog catalog = ReadCatalog(entry) ?? throw new InvalidDataException("The full-source catalog is absent.");
        if (catalog.Pending is not null || Exists(_fs.Path.Combine(Root(entry), ".redux-transaction.active")))
            throw new IOException("Installation recovery must finish before reading its published identity.");
        return catalog.Release;
    }

    /// <inheritdoc />
    public FullSourceInstallationStatus? Reconcile(Guid installationId, string? expectedInstallRoot = null)
    {
        Ksp2InstallEntry? configured = _configuration.Config.Ksp2Installs.SingleOrDefault(entry => entry.Id == installationId);
        if (configured is null)
            throw new InvalidDataException("The installation identity is not configured.");
        string root = _fs.Path.GetDirectoryName(configured.ExePath)!;
        ValidateExpectedRoot(root, expectedInstallRoot);
        if (!HasCatalog(root))
            return null;
        Ksp2InstallEntry entry = Entry(installationId, root);
        if (!Exists(HeadPath(entry)))
            return null;
        using FileSystemStream lease = Lock(entry);
        return ReconcileLocked(entry);
    }

    /// <inheritdoc />
    public void EnsureReady(string root)
    {
        if (!HasCatalog(root))
            return;
        Ksp2InstallEntry? entry = EntryAt(root);
        if (entry is null || !Exists(HeadPath(entry)))
            return;
        using FileSystemStream lease = Lock(entry);
        EnsureReadyLocked(entry);
    }

    /// <inheritdoc />
    public IDisposable? AcquireLaunchLease(string root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_os.IsWindows() && (!_os.IsLinux() || !HasCatalog(root)))
        {
            // Preserve catalog-free legacy host routes and reject unverified native adapters.
            EnsureReady(root);
            ct.ThrowIfCancellationRequested();
            return null;
        }
        Ksp2InstallEntry? entry = EntryAt(root);
        if (entry is null)
            return null;
        FileSystemStream lease = Lock(entry);
        try
        {
            if (EnsureReadyLocked(entry) is { } catalog)
                VerifyTree(Root(entry), Manifest(catalog.Ownership), ct);
            ct.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private Catalog? EnsureReadyLocked(Ksp2InstallEntry entry)
    {
        string root = Root(entry);
        if (!Exists(HeadPath(entry)))
            return null;
        Catalog catalog = ReadCatalog(entry)!;
        if (catalog.Pending is null)
        {
            if (Exists(_fs.Path.Combine(root, ".redux-transaction.active")))
                throw new IOException("An active transaction has no pending catalog authority. Evidence retained.");
            ProjectIdentity(entry, catalog);
            return catalog;
        }
        FullSourceInstallationStatus? status = ReconcileLocked(entry);
        if (status?.ArchiveFailure is { } failure)
            throw new IOException("Installation recovery completed, but the original stock backup needs attention. " +
                "The backup and recovery evidence have been preserved.", failure);
        return ReadCatalog(entry) ?? throw new InvalidDataException("The full-source catalog is absent.");
    }

    /// <inheritdoc />
    public async Task<FullSourceInstallationStatus> ApplyAsync(Guid installationId, FullSourcePreparedTarget target,
        FullSourceStockPreparation? stock, bool repair, Action<string> log, CancellationToken ct,
        string? expectedInstallRoot = null)
    {
        EnsureSupported();
        Ksp2InstallEntry entry = Entry(installationId, expectedInstallRoot);
        using FileSystemStream lease = Lock(entry);
        EnsureMutationAllowed(entry);
        FullSourceInstallationStatus? reconciled = ReconcileLocked(entry);
        if (reconciled?.ArchiveFailure is { } backupFailure && ReadCatalog(entry)?.Archive is not null)
            throw new IOException("The immutable original stock backup is unavailable or changed.", backupFailure);
        // Explicit repair preserves prior payloads instead of requiring the damaged generation to pass retirement.
        if (_os.IsWindows() && !repair)
            RetireCompletedPayloadsLocked(entry, ct);

        StockBaselineManifest? verifiedStage = null;
        if (_os.IsWindows() && _preparation.GetType() == typeof(FullSourcePreparationService))
            verifiedStage = await ((FullSourcePreparationService)_preparation).VerifyPreparedStageAsync(target, ct).ConfigureAwait(false);
        else
            await _preparation.VerifyTargetAsync(target, ct).ConfigureAwait(false);
        byte[] targetContract = target.Manifest.ToJson();
        _ = StockBaselineManifest.FromJson(targetContract);
        ValidateIdentity(target.Identity);
        if (Digest(targetContract) != target.Identity.ManifestSha256)
            throw new InvalidDataException("The independently verified release identity and manifest disagree.");
        ValidateInputRoot(entry, target.SourceRoot);
        Catalog? catalog = ReadCatalog(entry);
        if (target.InstalledBaseline is { } installed &&
            (!_os.IsWindows() || repair || target.AllowsDerivedBootConfig || installed.InstallationId != entry.Id ||
                catalog?.Release?.ManifestSha256 != Digest(installed.Manifest.ToJson())))
            throw new InvalidDataException("The installed generation changed after update preparation. Prepare the release again.");
        var retention = target.TargetRetention;
        if (retention is not null)
        {
            string? previous = retention.PreviousManifestSha256;
            bool matchesGeneration = retention.LegacyOwnershipSha256 is { } legacy
                ? catalog is null && !repair && previous is null && stock?.ExistingArchive is not null &&
                    stock.Release == target.Identity && legacy == Digest(stock.CurrentOwned.ToJson())
                : catalog is not null && repair == (previous is null) &&
                    (repair || string.Equals(catalog.Release?.ManifestSha256, previous, StringComparison.OrdinalIgnoreCase));
            if (!_os.IsWindows() || target.AllowsDerivedBootConfig || retention.InstallationId != entry.Id ||
                target.InstalledBaseline is not null || !matchesGeneration)
                throw new InvalidDataException("Prepared target retention does not belong to this installation and generation.");
        }
        bool fullPreparedStage = target.InstalledBaseline is null && retention is null;
        if (fullPreparedStage && verifiedStage is null)
            VerifyTree(target.SourceRoot, target.Manifest, ct);
        bool isInitialConversion = catalog is null;
        if (catalog is null)
        {
            if (repair || stock is null)
                throw new InvalidDataException("Initial full-source conversion requires independently verified stock and ownership.");
            bool adoptComplete = await VerifyStockPreparationAsync(entry, stock, ct).ConfigureAwait(false);
            if (Exists(_fs.Path.Combine(Root(entry), "uninstall.zip")) && stock.ExistingArchive is null)
                throw new IOException("An existing backup requires an explicit reviewed migration. It has been preserved.");
            if (stock.ExistingArchive is { } initialBackup && Exists(PreservedBackupPath(entry, initialBackup)))
                throw new IOException("Existing legacy archive retention storage has no catalog ownership. All files were preserved.");
            if (Exists(_fs.Path.Combine(Root(entry), ".redux-transaction.active")))
                throw new IOException("Uncataloged transaction evidence exists. It has been preserved.");
            catalog = new Catalog(FORMAT_VERSION, entry.Id, Root(entry), StorageRoot(entry), stock.Stock.ToJson(),
                stock.CurrentOwned.ToJson(), null, adoptComplete ? stock.ExistingArchive : null, stock.RetainedUnownedFiles.ToArray(),
                stock.UnownedDirectories.ToArray(), RETENTION, null, null, null, stock.ExistingArchive, stock.RecoverySources);
        }
        else if (stock is not null)
        {
            await VerifyStockPreparationAsync(entry, stock, ct).ConfigureAwait(false);
            if (!stock.Stock.ToJson().SequenceEqual(catalog.Stock))
                throw new InvalidDataException("Stock preparation differs from the original installation contract.");
        }

        StockBaselineManifest current = repair ? ObserveOwned(catalog, ct) : Manifest(catalog.Ownership);
        VerifyTree(Root(entry), current, ct);
        VerifyCollisions(catalog, current, target.Manifest, ct);
        StockBaselineManifest preparedLayout = target.Manifest;
        if (!fullPreparedStage)
        {
            preparedLayout = ChangedSourceContract(target.Manifest, current, catalog.RetainedFiles, false);
            if (retention?.Paths.Overlaps(preparedLayout.Files.Select(file => file.Path)) == true)
                throw new InvalidDataException("An installed file selected for reuse changed after preparation. Prepare the release again.");
            // Only reuse the scan when the live generation requires the exact authenticated stage.
            if (verifiedStage is null || !verifiedStage.ToJson().AsSpan().SequenceEqual(preparedLayout.ToJson()))
                VerifyTree(target.SourceRoot, preparedLayout, ct);
        }
        if (catalog.Archive is null && stock is null && catalog.ExistingBackup is null)
            throw new InvalidDataException("The original stock archive needs its separately verified source preparation.");
        bool moveSource = target.MayConsumeSource && !target.AllowsDerivedBootConfig && _os.IsWindows() &&
            _fs is RealFileSystem && _preparation is FullSourcePreparationService &&
            string.Equals(_fs.Path.GetPathRoot(target.SourceRoot), _fs.Path.GetPathRoot(StorageRoot(entry)), StringComparison.OrdinalIgnoreCase) &&
            HasExactSourceLayout(target.SourceRoot, preparedLayout, ct);
        EnsureMutationAllowed(entry);
        if (isInitialConversion)
            Write(HeadPath(entry), catalog, "catalog-initialize");
        Operation operation = Begin(entry, catalog, current, target.Manifest, target.Identity, target.AllowsDerivedBootConfig, repair,
            fullDirectorySource: moveSource && fullPreparedStage);
        Catalog previousCatalog = catalog;
        catalog = catalog with { Pending = operation.TransactionId };
        Write(HeadPath(entry), catalog, "catalog-pending", previousCatalog);
        try
        {
            log("Preparing the verified original backup and release files...");
            StockBaselineFile archive = await EnsureArchiveAsync(entry, catalog, stock, ct).ConfigureAwait(false);
            Boundary("stage-create", () =>
            {
                _engine.ValidateControlledPath(operation.SourceRoot);
                if (Exists(operation.SourceRoot))
                    throw new InvalidDataException("The new owned source stage already exists. Evidence retained.");
                if (moveSource)
                {
                    _engine.ValidateControlledPath(target.SourceRoot);
                    if (!HasExactSourceLayout(target.SourceRoot, preparedLayout, ct))
                        throw new InvalidDataException("The prepared source changed before transfer. Evidence retained.");
                    _fs.Directory.Move(target.SourceRoot, operation.SourceRoot);
                }
                else
                    StockArchive.CreateDirectory(_fs, operation.SourceRoot);
            });
            if (!moveSource)
                CopyTree(target.SourceRoot, operation.SourceRoot, SourceContract(catalog, operation), ct);
            StockBaselineManifest targetGeneration = DeriveBootConfig(operation, target.Manifest);
            operation = operation with { Target = targetGeneration.ToJson(), Archive = archive };
            return ApplyPrepared(entry, catalog, operation, log, ct);
        }
        catch (OperationCanceledException failure)
        {
            RecoverCancellation(entry, failure);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FullSourceInstallationStatus> UninstallAsync(Guid installationId, Action<string> log, CancellationToken ct,
        string? expectedInstallRoot = null)
    {
        EnsureSupported();
        Ksp2InstallEntry entry = Entry(installationId, expectedInstallRoot);
        using FileSystemStream lease = Lock(entry);
        EnsureMutationAllowed(entry);
        FullSourceInstallationStatus status = ReconcileLocked(entry)
            ?? throw new InvalidDataException("The installation has no full-source catalog.");
        if (status.ArchiveFailure is { } archiveFailure)
            throw new IOException("Original stock restoration requires a healthy verified backup.", archiveFailure);
        if (_os.IsWindows())
            RetireCompletedPayloadsLocked(entry, ct);
        Catalog catalog = ReadCatalog(entry)!;
        StockBaselineManifest current = Manifest(catalog.Ownership);
        VerifyTree(Root(entry), current, ct);
        StockBaselineManifest stock = Manifest(catalog.Stock);
        VerifyCollisions(catalog, current, stock, ct);
        EnsureMutationAllowed(entry);
        Operation operation = Begin(entry, catalog, current, stock, null, false, stockArchiveSource: _os.IsWindows());
        Catalog previousCatalog = catalog;
        catalog = catalog with { Pending = operation.TransactionId };
        Write(HeadPath(entry), catalog, "catalog-pending", previousCatalog);
        try
        {
            StockArchive.CreateDirectory(_fs, StorageRoot(entry));
            if (operation.StockArchiveSource)
            {
                log("Staging original game files directly from the verified stock backup...");
                StockArchive.CreateDirectory(_fs, operation.SourceRoot);
            }
            else
            {
                log("Restoring the original game into a separate verified stage...");
                await _archive.RestoreToNewDirectoryAsync(catalog.Archive!.Path, stock, operation.SourceRoot, ct).ConfigureAwait(false);
            }
            operation = operation with { Archive = catalog.Archive };
            return ApplyPrepared(entry, catalog, operation, log, ct);
        }
        catch (OperationCanceledException failure)
        {
            RecoverCancellation(entry, failure);
            throw;
        }
    }

    private FullSourceInstallationStatus ApplyPrepared(Ksp2InstallEntry entry, Catalog catalog, Operation operation,
        Action<string> log, CancellationToken ct)
    {
        VerifyTree(operation.SourceRoot, SourceContract(catalog, operation), ct);
        InstallTransactionPlan plan = BuildPlan(entry, catalog, operation);
        Operation preparing = ReadOperation(entry, catalog);
        // Archive creation and source staging can outlast the first use check.
        EnsureMutationAllowed(entry);
        operation = operation with { Phase = "ready", PlanDigest = plan.GetDigest() };
        Write(OperationPath(entry, operation.TransactionId), operation, "operation-ready", preparing);
        log("Applying the verified game generation...");
        InstallTransactionResult result = default;
        Boundary("engine-apply", () => result = _engine.Apply(plan, ct));
        Operation ready = operation;
        operation = operation with { Phase = "terminal", Result = result };
        Write(OperationPath(entry, operation.TransactionId), operation, "operation-result", ready);
        Catalog published = Publish(entry, catalog, operation, result);
        log("The game generation was verified and saved.");
        // Defer optional cleanup when cancellation is already known after publication.
        if (_os.IsWindows() && !ct.IsCancellationRequested)
            RetireCompletedPayloadsLocked(entry, ct);
        return Status(published, null);
    }

    private Operation Begin(Ksp2InstallEntry entry, Catalog catalog, StockBaselineManifest current,
        StockBaselineManifest target, FullSourceReleaseIdentity? identity, bool derivedBoot, bool repair = false,
        bool stockArchiveSource = false, bool fullDirectorySource = false)
    {
        Guid id = Guid.NewGuid();
        if (Exists(SourceRoot(entry, id)) || Exists(WorkRoot(entry, id)) || Exists(OperationPath(entry, id)) ||
            Exists(BackupCandidatePath(entry, id)))
            throw new InvalidDataException("New operation storage conflicts with retained evidence.");
        string storage = StorageRoot(entry);
        _engine.ValidateControlledPath(storage);
        StockArchive.CreateDirectory(_fs, storage);
        // New source and work names are absent canonical GUID siblings. Native identity checks reject
        // aliases and reparse points, so retained sibling metadata needs no ever-growing protected list.
        string[] protectedRoots = _configuration.Config.Ksp2Installs.Where(other => other.Id != entry.Id).Select(Root)
            .Concat(_os.IsWindows() ? [] : _fs.Directory.EnumerateDirectories(storage))
            .Append(CatalogRoot(entry))
            .Order(StringComparer.Ordinal).ToArray();
        if (protectedRoots.Length > 1024)
            throw new IOException("Retained operation storage needs reviewed retirement before another operation.");
        var targetFiles = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var directories = current.Directories.Concat(target.Directories).ToHashSet(StringComparer.Ordinal);
        var operation = new Operation(FORMAT_VERSION, id, entry.Id, Root(entry), SourceRoot(entry, id), WorkRoot(entry, id),
            Digest(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(catalog, JSON_OPTIONS)), current.ToJson(), target.ToJson(),
            target.ToJson(), identity, derivedBoot, entry.DisableGraphicsJobs, null, repair, protectedRoots,
            catalog.Archive, null, "preparing", null, ChangedFileStaging: true,
            SparseSourceStaging: identity is not null && _os.IsWindows() && !fullDirectorySource, StockArchiveSource: stockArchiveSource,
            MovedFileUndo: _os.IsWindows(),
            OwnedSourceStaging: identity is not null && _os.IsWindows() && _fs is RealFileSystem);
        _ = new InstallTransactionPlan(id, Root(entry), operation.SourceRoot, operation.WorkRoot, current, target,
            Manifest(catalog.Stock), catalog.Archive ?? new StockBaselineFile(_fs.Path.Combine(Root(entry), "uninstall.zip"),
                1, new string('0', 64)), catalog.RetainedFiles.Where(targetFiles.Contains),
                catalog.UnownedDirectories.Where(directories.Contains), protectedRoots, operation.ChangedFileStaging,
                operation.StockArchiveSource, operation.MovedFileUndo, operation.OwnedSourceStaging);
        Write(OperationPath(entry, id), operation, "operation-prepare");
        return operation;
    }

    private FullSourceInstallationStatus? ReconcileLocked(Ksp2InstallEntry entry)
    {
        Catalog? catalog = ReadCatalog(entry);
        if (catalog is null)
            return null;
        if (catalog.Pending is null)
        {
            if (Exists(_fs.Path.Combine(Root(entry), ".redux-transaction.active")))
                throw new IOException("An active transaction has no pending catalog authority. Evidence retained.");
            ProjectIdentity(entry, catalog);
            return Status(catalog, ArchiveHealth(catalog));
        }
        EnsureMutationAllowed(entry);
        Operation operation = ReadOperation(entry, catalog);
        InstallTransactionResult result;
        Exception? archiveFailure;
        if (operation.Phase == "preparing")
        {
            if (Exists(_fs.Path.Combine(Root(entry), ".redux-transaction.active")) || Exists(operation.WorkRoot))
                throw new InvalidDataException("Engine evidence conflicts with preparation-only catalog authority. Evidence retained.");
            VerifyTree(Root(entry), Manifest(operation.Current), CancellationToken.None);
            if (catalog.ExistingBackup is not null)
                operation = CompletePendingBackupPromotion(entry, catalog, operation);
            result = InstallTransactionResult.RolledBack;
            archiveFailure = ArchiveHealth(catalog);
            if (catalog.Archive is null && Exists(_fs.Path.Combine(Root(entry), "uninstall.zip")))
            {
                try
                {
                    string path = _fs.Path.Combine(Root(entry), "uninstall.zip");
                    _engine.ValidateControlledPath(path);
                    StockBaselineFile verified = Task.Run(() => _archive.VerifyAndPinAsync(path, Manifest(catalog.Stock)))
                        .GetAwaiter().GetResult();
                    operation = operation with { Archive = verified };
                    archiveFailure = null;
                }
                catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    archiveFailure = failure;
                }
            }
        }
        else
        {
            (result, archiveFailure) = _engine.RecoverWithArchiveHealth(BuildPlan(entry, catalog, operation));
            if (operation.Result is { } recorded && recorded != result)
                throw new InvalidDataException("The recorded result conflicts with verified engine evidence.");
        }
        // Even an unhealthy backup must not hide a committed or restored generation.
        if (operation.Phase != "preparing")
        {
            Operation previousOperation = operation;
            operation = operation with { Phase = "terminal", Result = result };
            Write(OperationPath(entry, operation.TransactionId), operation, "operation-recovered", previousOperation);
        }
        Catalog published = Publish(entry, catalog, operation, result);
        _log.Info($"Reconciled full-source installation {entry.Id}: {result}.");
        if (archiveFailure is not null)
            _log.Error("The original stock backup failed its independent health check.", archiveFailure);
        if (_os.IsWindows() && archiveFailure is null)
            RetireCompletedPayloadsLocked(entry, CancellationToken.None);
        return Status(published, archiveFailure);
    }

    private void EnsureMutationAllowed(Ksp2InstallEntry entry)
    {
        string root = Root(entry);
        if (_gameProcesses.IsRunning(root))
            throw new IOException("KSP2 is running from the intended installation or its process identity cannot be inspected. " +
                "Close the game before changing or recovering this installation. Installation and recovery evidence were preserved.");
        _fileLocks.EnsureNoFilesInUse(root, true);
        // Recheck after the file scan, which may retry a transient lock.
        if (_gameProcesses.IsRunning(root))
            throw new IOException("KSP2 started while installation file use was being checked. Installation and recovery evidence were preserved.");
        Ksp2InstallEntry? configured = _configuration.Config.Ksp2Installs.SingleOrDefault(candidate => candidate.Id == entry.Id);
        if (configured is null)
            throw new InvalidDataException("The installation identity is no longer configured.");
        ValidateExpectedRoot(Root(configured), root);
    }

    private Catalog Publish(Ksp2InstallEntry entry, Catalog catalog, Operation operation, InstallTransactionResult result)
    {
        byte[] ownership = catalog.Ownership;
        FullSourceReleaseIdentity? release = catalog.Release;
        StockBaselineManifest target = Manifest(operation.Target);
        var targetDirectories = target.Directories.ToHashSet(StringComparer.Ordinal);
        var retained = catalog.RetainedFiles.ToHashSet(StringComparer.Ordinal);
        if (result == InstallTransactionResult.Committed)
        {
            ownership = new StockBaselineManifest(target.BaselineIdentity, target.Provenance,
                target.Files.Where(file => !retained.Contains(file.Path)), target.Directories,
                target.HasUnixMetadata ? target.UnixDirectoryModes : null).ToJson();
            release = operation.Release;
        }
        Catalog published = catalog with
        {
            Ownership = ownership, Release = release, Archive = operation.Archive ?? catalog.Archive,
            Pending = null, LastResult = result, LastTransaction = operation.TransactionId,
            UnownedDirectories = result == InstallTransactionResult.Committed
                ? catalog.UnownedDirectories.Concat(Manifest(catalog.Ownership).Directories
                    .Where(path => !targetDirectories.Contains(path) &&
                        _fs.Directory.Exists(At(catalog.InstallRoot, path))))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                : catalog.UnownedDirectories
        };
        Write(HeadPath(entry), published, "catalog-ownership", catalog);
        ProjectIdentity(entry, published);
        return published;
    }

    private void ProjectIdentity(Ksp2InstallEntry entry, Catalog catalog)
    {
        Ksp2InstallEntry? configured = _configuration.Config.Ksp2Installs.SingleOrDefault(candidate => candidate.Id == entry.Id);
        if (configured is null || !string.Equals(configured.ExePath, entry.ExePath,
                _os.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;

        // A rolled-back legacy observation has no published full-source release identity.
        if (catalog.ExistingBackup is not null && catalog.Release is null &&
            catalog.LastResult != InstallTransactionResult.Committed && !catalog.Ownership.SequenceEqual(catalog.Stock))
            return;
        GameVersion? version = null;
        if (catalog.Release is { } release)
        {
            string[] components = release.Version.Split('.');
            version = new GameVersion
            {
                VersionNumber = Version.Parse(string.Join('.', components[..4])),
                BuildNumber = components[4], Channel = release.Channel
            };
        }
        if ((configured.LastInstalledVersion?.Equals(version) == true && configured.LastInstalledVersion.Channel == version!.Channel) ||
            (version is null && configured.LastInstalledVersion is null))
            return;
        // Configuration is a UI projection. Its save can fail without revoking the durable catalog result.
        Boundary("identity-projection", () =>
        {
            configured.LastInstalledVersion = version;
            if (catalog.Release is { } identity)
                configured.ReleaseChannel = identity.Channel;
            _configuration.Save();
        });
    }

    private Exception? ArchiveHealth(Catalog catalog)
    {
        if (catalog.Archive is not { } archive)
            return new IOException("The complete original stock archive has not yet been published.");
        try
        {
            _engine.ValidateControlledPath(archive.Path);
            StockBaselineFile actual = ObserveFile(archive.Path, archive.Path, CancellationToken.None);
            if (actual.Length != archive.Length || actual.Sha256 != archive.Sha256)
                throw new InvalidDataException("The immutable original stock archive bytes changed.");
            return null;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return failure;
        }
    }

    private async Task<StockBaselineFile> EnsureArchiveAsync(Ksp2InstallEntry entry, Catalog catalog,
        FullSourceStockPreparation? stock, CancellationToken ct)
    {
        // Recovery or initial stock admission checked this pin. Apply reopens and verifies it before any mutation.
        if (catalog.Archive is { } existing)
            return existing;

        if (catalog.ExistingBackup is not null)
            return await PrepareExistingBackupAsync(entry, catalog, ct).ConfigureAwait(false);
        string path = _fs.Path.Combine(Root(entry), "uninstall.zip");
        StockBaselineManifest expected = Manifest(catalog.Stock);
        if (!Exists(path))
        {
            if (stock is null)
                throw new InvalidDataException("Verified original stock source preparation is required.");
            EnsureMutationAllowed(entry);
            _boundary?.Invoke("archive-create", false);
            StockBaselineFile pin = await _archive.CreateWithPinAsync(stock.SourceRoot, path, expected, ct)
                .ConfigureAwait(false);
            _boundary?.Invoke("archive-create", true);
            RequireBackupPin(pin, ct);
            return pin;
        }

        _engine.ValidateControlledPath(path);
        return await _archive.VerifyAndPinAsync(path, expected, ct).ConfigureAwait(false);
    }

    private async Task<bool> VerifyStockPreparationAsync(Ksp2InstallEntry entry, FullSourceStockPreparation stock, CancellationToken ct)
    {
        if (stock.InstallationId is { } preparedId && preparedId != entry.Id)
            throw new InvalidDataException("Initial stock ownership preparation belongs to a different configured installation.");
        await _preparation.VerifyStockAsync(stock, ct).ConfigureAwait(false);
        _ = StockBaselineManifest.FromJson(stock.Stock.ToJson());
        _ = StockBaselineManifest.FromJson(stock.CurrentOwned.ToJson());
        ValidatePaths(stock.RetainedUnownedFiles.ToArray());
        ValidatePaths(stock.UnownedDirectories.ToArray(), true);
        VerifyTree(Root(entry), stock.CurrentOwned, ct);
        if (stock.ExistingArchive is { } backup)
        {
            if (!_os.IsWindows() || stock.SourceRoot != "")
                throw new InvalidDataException("Existing-backup preparation requires the Windows archive source path.");
            ValidateArchiveReference(entry, backup);
            RequireBackupPin(backup, ct);
            ValidateRecoveryInputPaths(entry, stock.Stock);
            StockArchiveSources sources = await _archive.InspectAvailableSourcesAsync(backup.Path, Root(entry), stock.Stock,
                ct, stock.RecoverySources).ConfigureAwait(false);
            if (sources.Journal is not null && stock.RecoverySources is null)
                throw new InvalidDataException("Journal recovery requires source pins saved by preparation.");
            return sources.CanonicalArchive;
        }
        bool installedSource = _os.IsWindows() && stock.InstallationId == entry.Id && stock.SourceRoot == Root(entry);
        if (!installedSource)
            ValidateInputRoot(entry, stock.SourceRoot);
        VerifyTree(stock.SourceRoot, stock.Stock, ct);
        return false;
    }

    private void ValidateInputRoot(Ksp2InstallEntry entry, string root)
    {
        _engine.ValidateControlledPath(root);
        if (!_fs.Directory.Exists(root) || Overlaps(root, Root(entry)) || Overlaps(root, StorageRoot(entry)) ||
            Overlaps(root, CatalogRoot(entry)))
            throw new InvalidDataException("Prepared input must be separate from installation, catalog and owned operation storage.");
    }

    private void VerifyTree(string root, StockBaselineManifest manifest, CancellationToken ct)
    {
        if (_os.IsLinux() && !manifest.HasUnixMetadata)
            throw new InvalidDataException("Linux generation contracts require complete version-two Unix metadata.");
        foreach (string directory in manifest.Directories)
        {
            ct.ThrowIfCancellationRequested();
            string path = At(root, directory);
            _engine.ValidateControlledPath(path);
            if (!_fs.Directory.Exists(path))
                throw new InvalidDataException($"An expected generation directory is missing: '{directory}'.");
            if (manifest.HasUnixMetadata && !StockArchive.UnixPermissionsMatch(_fs, path, manifest.UnixDirectoryModes[directory]))
                throw new InvalidDataException($"Generation directory permissions differ from their contract: '{directory}'.");
        }
        foreach (StockBaselineFile file in manifest.Files)
        {
            StockBaselineFile actual = ObserveFile(At(root, file.Path), file.Path, ct, file.UnixMode);
            if (actual != file)
                throw new InvalidDataException($"Generation bytes differ from their independently expected contract: '{file.Path}'.");
        }
    }

    private void VerifyCollisions(Catalog catalog, StockBaselineManifest current, StockBaselineManifest target, CancellationToken ct)
    {
        if (_os.IsLinux())
        {
            foreach (string directory in current.Directories.Intersect(target.Directories))
            {
                if (current.UnixDirectoryModes[directory] != target.UnixDirectoryModes[directory])
                    throw new NotSupportedException("Existing Unix directory permission changes require a metadata transaction before preparation.");
            }
        }
        var currentFiles = current.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var currentDirectories = current.Directories.ToHashSet(StringComparer.Ordinal);
        var retained = catalog.RetainedFiles.ToHashSet(StringComparer.Ordinal);
        var unownedDirectories = catalog.UnownedDirectories.ToHashSet(StringComparer.Ordinal);
        foreach (StockBaselineFile file in target.Files.Where(file => !currentFiles.Contains(file.Path)))
        {
            string path = At(catalog.InstallRoot, file.Path);
            _engine.ValidateControlledPath(path);
            if (retained.Contains(file.Path))
            {
                if (ObserveFile(path, file.Path, ct, file.UnixMode) != file)
                    throw new InvalidDataException("An explicitly retained unowned target differs from its contract.");
            }
            else if (Exists(path))
            {
                throw new InvalidDataException($"The target collides with unowned file '{file.Path}'. Its bytes were preserved. " +
                    "Legacy installer additions require an independently published legacy ownership contract.");
            }
        }
        foreach (string directory in target.Directories.Where(path => !currentDirectories.Contains(path)))
        {
            string path = At(catalog.InstallRoot, directory);
            _engine.ValidateControlledPath(path);
            if (Exists(path) && !unownedDirectories.Contains(directory))
                throw new InvalidDataException("The target collides with an unowned directory lacking an explicit retention policy.");
        }
    }

    private StockBaselineFile ObserveFile(string path, string relative, CancellationToken ct, int? expectedUnixMode = null)
    {
        _engine.ValidateControlledPath(path);
        using FileSystemStream input = _fs.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(OBSERVATION_BUFFER_BYTES);
        long length = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer.AsSpan(0, OBSERVATION_BUFFER_BYTES))) != 0)
            {
                ct.ThrowIfCancellationRequested();
                length = checked(length + read);
                if (length > 130L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Observed bytes exceed the operation budget.");
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        int? mode = expectedUnixMode is null ? null : StockArchive.ReadUnixPermissions(_fs, path) ?? expectedUnixMode;
        return new StockBaselineFile(relative, length, Convert.ToHexString(hash.GetHashAndReset()), mode);
    }

    private StockBaselineManifest ObserveOwned(Catalog catalog, CancellationToken ct)
    {
        StockBaselineManifest owned = Manifest(catalog.Ownership);
        var files = new List<StockBaselineFile>();
        foreach (StockBaselineFile file in owned.Files)
        {
            string path = At(catalog.InstallRoot, file.Path);
            _engine.ValidateControlledPath(path);
            if (Exists(path))
                files.Add(ObserveFile(path, file.Path, ct, file.UnixMode));
        }
        string[] directories = owned.Directories.Where(path => _fs.Directory.Exists(At(catalog.InstallRoot, path))).ToArray();
        return new StockBaselineManifest($"{owned.BaselineIdentity}:repair-observation", "explicit owned-file repair observation", files, directories,
            owned.HasUnixMetadata ? directories.ToDictionary(path => path, path => StockArchive.ReadUnixPermissions(_fs, At(catalog.InstallRoot, path))
                ?? owned.UnixDirectoryModes[path], StringComparer.Ordinal) : null);
    }

    private bool HasExactSourceLayout(string root, StockBaselineManifest manifest, CancellationToken ct)
    {
        var files = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var directories = manifest.Directories.ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in _fs.Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                string relative = _fs.Path.GetRelativePath(root, path).Replace('\\', '/');
                FileAttributes attributes = _fs.File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Remove(relative))
                        return false;
                    pending.Push(path);
                }
                else if (!files.Remove(relative))
                    return false;
            }
        }
        return files.Count == 0 && directories.Count == 0;
    }

    private void CopyTree(string inputRoot, string destination, StockBaselineManifest manifest, CancellationToken ct)
    {
        foreach (string directory in manifest.Directories)
        {
            ct.ThrowIfCancellationRequested();
            _engine.ValidateControlledPath(At(destination, directory));
            StockArchive.CreateDirectory(_fs, At(destination, directory));
            if (manifest.HasUnixMetadata)
                StockArchive.ApplyUnixPermissions(_fs, At(destination, directory), manifest.UnixDirectoryModes[directory]);
            StockArchive.FlushLinuxDirectory(_fs, _fs.Path.GetDirectoryName(At(destination, directory))!);
        }
        foreach (StockBaselineFile file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string source = At(inputRoot, file.Path);
            string target = At(destination, file.Path);
            _engine.ValidateControlledPath(source);
            _engine.ValidateControlledPath(target);
            using FileSystemStream input = _fs.FileStream.New(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using FileSystemStream output = _fs.FileStream.New(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            output.Flush(true);
            if (file.UnixMode is not null)
                StockArchive.ApplyUnixPermissions(_fs, target, file.UnixMode.Value);
            StockArchive.FlushLinuxDirectory(_fs, _fs.Path.GetDirectoryName(target)!);
        }
        VerifyTree(destination, manifest, ct);
    }

    private StockBaselineManifest DeriveBootConfig(Operation operation, StockBaselineManifest release)
    {
        if (!operation.DerivedBootConfig)
            return release;
        if (release.Files.All(file => file.Path != BOOT_CONFIG))
            throw new InvalidDataException("Authenticated derived boot configuration requires its release-owned source file.");
        string path = At(operation.SourceRoot, BOOT_CONFIG);
        if (_fs.FileInfo.New(path).Length > 1024 * 1024)
            throw new InvalidDataException("Derived boot configuration exceeds its byte budget.");
        byte[] sourceBytes = _fs.File.ReadAllBytes(path);
        int offset = sourceBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        string text = new UTF8Encoding(false, true).GetString(sourceBytes.AsSpan(offset));
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        List<(string Key, string Value)> settings = new()
        {
            ("gfx-enable-gfx-jobs", operation.DisableGraphicsJobs ? "0" : "1")
        };
        // Older journals retain the exact HDR choice used to derive their recorded target bytes.
        if (operation.EnableHdr is { } legacyHdr)
            settings.Add(("hdr-display-enabled", legacyHdr ? "1" : "0"));
        foreach ((string key, string value) in settings)
        {
            int index = lines.FindIndex(line => line.StartsWith($"{key}=", StringComparison.Ordinal));
            if (lines.Count(line => line.StartsWith($"{key}=", StringComparison.Ordinal)) > 1)
                throw new InvalidDataException("Derived boot configuration has duplicate settings.");
            if (index < 0)
                lines.Add($"{key}={value}");
            else
                lines[index] = $"{key}={value}";
        }
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join(newline, lines) + newline);
        int? permissions = release.Files.Single(file => file.Path == BOOT_CONFIG).UnixMode;
        if (permissions is not null)
            StockArchive.ApplyUnixPermissions(_fs, path, 384);
        using (FileSystemStream output = _fs.FileStream.New(path, FileMode.Truncate, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(true);
        }
        if (permissions is not null)
            StockArchive.ApplyUnixPermissions(_fs, path, permissions.Value);
        StockBaselineFile derived = new(BOOT_CONFIG, bytes.LongLength, Digest(bytes), permissions);
        return new StockBaselineManifest($"{release.BaselineIdentity}:boot-v1:{derived.Sha256}",
            "authenticated release with explicit launcher boot configuration v1", release.Files.Select(file =>
                file.Path == BOOT_CONFIG ? derived : file), release.Directories,
            release.HasUnixMetadata ? release.UnixDirectoryModes : null);
    }

    private FullSourceInstallationStatus Status(Catalog catalog, Exception? archiveFailure) =>
        new(catalog.InstallationId, Manifest(catalog.Ownership), catalog.Release, catalog.LastResult, archiveFailure);

    private string At(string root, string path) => _fs.Path.Combine(root, path.Replace('/', _fs.Path.DirectorySeparatorChar));

    private void EnsureSupported()
    {
        if (!_os.IsWindows() && !_os.IsLinux())
            throw new PlatformNotSupportedException("Full-source transactions require a verified Windows or Linux native adapter. " +
                "Existing legacy host routes remain supported.");
    }

    private void RecoverCancellation(Ksp2InstallEntry entry, OperationCanceledException failure)
    {
        try
        {
            failure.Data["FullSourceRecovery"] = ReconcileLocked(entry);
        }
        catch (Exception recoveryFailure)
        {
            failure.Data["FullSourceRecoveryFailure"] = recoveryFailure;
        }
    }
}
