using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourceInstallationService
{
    private const int FORMAT_VERSION = 1;
    private const int MAX_CATALOG_BYTES = 96 * 1024 * 1024;
    private const string RETENTION = "retain-contracts-v1";
    private const string LEGACY_RETENTION = "retain-all-v1";
    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    // These shapes are launcher-owned persisted contracts, separate from untrusted engine journals.
    private record Catalog(int FormatVersion, Guid InstallationId, string InstallRoot, string StorageRoot,
        byte[] Stock, byte[] Ownership, FullSourceReleaseIdentity? Release, StockBaselineFile? Archive,
        string[] RetainedFiles, string[] UnownedDirectories, string RetentionPolicy, Guid? Pending,
        InstallTransactionResult? LastResult, Guid? LastTransaction,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StockBaselineFile? ExistingBackup = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StockArchiveSources? RecoverySources = null,
        // The boot.config settings of the committed release. Null for catalogs written before they were recorded.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? ReleaseBootConfig = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ReleaseIdentityMigrationPending = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? StockBootConfigRestore = null)
    {
        // The catalog exactly as an earlier build stored it, while an operation of that build is still pending.
        // Only its pending operation and publication compare against these bytes. It is never persisted.
        [JsonIgnore]
        public Catalog? LegacyStored { get; init; }
    }

    private record Operation(int FormatVersion, Guid TransactionId, Guid InstallationId, string InstallRoot,
        string SourceRoot, string WorkRoot, string PreviousDigest, byte[] Current, byte[] Target,
        byte[] ReleaseManifest, FullSourceReleaseIdentity? Release,
        // Retired boot derivation fields. Older journals still carry them, so they stay readable. New journals write false.
        bool DerivedBootConfig, bool DisableGraphicsJobs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? EnableHdr,
        bool IsRepair, string[] ProtectedRoots, StockBaselineFile? Archive,
        string? PlanDigest, string Phase, InstallTransactionResult? Result,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ChangedFileStaging = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SparseSourceStaging = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool StockArchiveSource = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool MovedFileUndo = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OwnedSourceStaging = false,
        // The release settings for a Redux target, or the original settings to restore for a stock target.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? BootConfigSettings = null);

    private record Envelope(int FormatVersion, string Sha256, byte[] Payload);

    private Ksp2InstallEntry Entry(Guid id, string? expectedInstallRoot = null)
    {
        Ksp2InstallEntry[] matches = _configuration.Config.Ksp2Installs.Where(entry => entry.Id == id).ToArray();
        if (id == Guid.Empty || matches.Length != 1)
            throw new InvalidDataException("A unique configured installation identity is required.");
        // The profile can be edited while an operation is waiting on I/O.
        var entry = new Ksp2InstallEntry
        {
            Id = id,
            ExePath = matches[0].ExePath
        };
        string root = Root(entry);
        ValidateExpectedRoot(root, expectedInstallRoot);
        foreach (Ksp2InstallEntry other in _configuration.Config.Ksp2Installs.Where(entry => entry.Id != id))
        {
            if (Overlaps(root, Root(other)))
                throw new InvalidDataException("Configured installation roots overlap or alias each other.");
        }
        return entry;
    }

    private Ksp2InstallEntry? EntryAt(string root)
    {
        // Unconfigured paths can receive protection checks, but never recovery authority.
        Ksp2InstallEntry[] matches = _configuration.Config.Ksp2Installs
            .Where(entry => _fs.Path.GetDirectoryName(entry.ExePath)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException("The installation root has multiple configured identities.");
        return matches.Length == 0 ? null : Entry(matches[0].Id, root);
    }

    private void ValidateExpectedRoot(string currentRoot, string? expectedRoot)
    {
        if (expectedRoot is not null && !string.Equals(currentRoot, expectedRoot,
                _os.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The configured installation folder changed. Select the installation again.");
    }

    private string Root(Ksp2InstallEntry entry)
    {
        _engine.ValidateControlledPath(entry.ExePath);
        if (_fs.Path.GetFileName(entry.ExePath) != Ksp2Install.KSP2_EXE_NAME)
            throw new InvalidDataException("The configured executable identity is not KSP2.");
        string root = _fs.Path.GetDirectoryName(entry.ExePath)!;
        _engine.ValidateControlledPath(root);
        return root;
    }

    private string CatalogRoot(Ksp2InstallEntry entry)
    {
        string path = _fs.Path.Combine(_configuration.GetLocalStorageDirectory(), "full-source-v1", entry.Id.ToString("N"));
        _engine.ValidateControlledPath(path);
        if (_configuration.Config.Ksp2Installs.Any(other => Overlaps(path, Root(other))) || Overlaps(path, StorageRoot(entry)))
            throw new InvalidDataException("Launcher catalog storage overlaps writable installation storage.");
        return path;
    }

    private string StorageRoot(Ksp2InstallEntry entry)
    {
        string path = _fs.Path.Combine(_fs.Path.GetDirectoryName(Root(entry))!, $".redux-fullsource-{entry.Id:N}");
        _engine.ValidateControlledPath(path);
        if (_configuration.Config.Ksp2Installs.Any(other => Overlaps(path, Root(other))))
            throw new InvalidDataException("Owned operation storage overlaps a configured installation. All bytes retained.");
        return path;
    }

    private string HeadPath(Ksp2InstallEntry entry) => _fs.Path.Combine(CatalogRoot(entry), "installation.json");

    private string OperationPath(Ksp2InstallEntry entry, Guid id) => _fs.Path.Combine(CatalogRoot(entry), $"{id:N}.json");

    private string SourceRoot(Ksp2InstallEntry entry, Guid id) => _fs.Path.Combine(StorageRoot(entry), $"{id:N}-source");

    private string WorkRoot(Ksp2InstallEntry entry, Guid id) => _fs.Path.Combine(StorageRoot(entry), $"{id:N}-work");

    private Catalog? ReadCatalog(Ksp2InstallEntry entry)
    {
        string path = HeadPath(entry);
        if (!Exists(path))
            return null;
        Catalog stored = Read<Catalog>(path);
        if (stored.Stock is null || stored.Ownership is null || stored.RetainedFiles is null || stored.UnownedDirectories is null)
            throw new InvalidDataException("Required catalog contracts or ownership policy are missing. Evidence retained.");
        Catalog catalog = NormalizeLegacyContracts(entry, stored);
        if (catalog.FormatVersion != FORMAT_VERSION || catalog.InstallationId != entry.Id ||
            catalog.InstallRoot != Root(entry) || catalog.StorageRoot != StorageRoot(entry) ||
            catalog.RetentionPolicy is not (RETENTION or LEGACY_RETENTION) || catalog.Pending == Guid.Empty || catalog.LastTransaction == Guid.Empty ||
            catalog.LastResult is { } lastResult && !Enum.IsDefined(lastResult))
            throw new InvalidDataException("The catalog identity, root, format or retention policy is invalid. Evidence retained.");
        _engine.ValidateControlledPath(catalog.StorageRoot);
        StockBaselineManifest stock = Manifest(catalog.Stock);
        StockBaselineManifest owned = Manifest(catalog.Ownership);
        ValidatePaths(catalog.RetainedFiles);
        ValidatePaths(catalog.UnownedDirectories, true);
        var retained = catalog.RetainedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (owned.Files.Any(file => retained.Contains(file.Path)))
            throw new InvalidDataException("Retained unowned files cannot belong to managed ownership.");
        if (catalog.Release is { } identity)
            ValidateIdentity(identity);
        if (catalog.ReleaseBootConfig is { } bootConfig)
            ValidateBootConfigSettings(bootConfig);
        if (catalog.StockBootConfigRestore is { } stockSettings)
        {
            if (catalog.Release is not null)
                throw new InvalidDataException("A Redux generation cannot have a stock boot configuration restore.");
            ValidateBootConfigSettings(stockSettings);
        }
        if (catalog.Archive is { } archive)
            ValidateArchiveReference(entry, archive);
        if (catalog.ExistingBackup is { } existingBackup)
        {
            ValidateArchiveReference(entry, existingBackup);
            _engine.ValidateControlledPath(PreservedBackupPath(entry, existingBackup));
        }
        if (catalog.RecoverySources is not null && catalog.ExistingBackup is null)
            throw new InvalidDataException("Recovery source pins require an existing original backup.");
        // Constructing a plan applies the reserved-name, union and resource checks to catalog ownership too.
        _ = new InstallTransactionPlan(Guid.NewGuid(), catalog.InstallRoot, SourceRoot(entry, Guid.NewGuid()),
            WorkRoot(entry, Guid.NewGuid()), owned, stock, stock,
            catalog.Archive ?? new StockBaselineFile(_fs.Path.Combine(catalog.InstallRoot, "uninstall.zip"), 1, new string('0', 64)));
        // Persist the migration only after every check above passed, so later reads find the canonical form.
        if (ReferenceEquals(catalog, stored))
            return catalog;
        // The pending operation of an earlier build is bound to the bytes of the catalog it began from. It is rolled back
        // or refused by reconciliation, which publishes the canonical form. Until then the old bytes stay.
        if (stored.Pending is not null)
            return catalog with { LegacyStored = stored };
        Write(path, catalog, "catalog-migrate", stored);
        _log.Info($"Migrated the full-source catalog of installation {entry.Id}: boot.config left its contracts.");
        return catalog;
    }

    // Catalogs written before boot.config left every contract list it in Stock and Ownership, and name the release by
    // the digest of a contract that lists it. Those bytes read as the contracts without that entry.
    private Catalog NormalizeLegacyContracts(Ksp2InstallEntry entry, Catalog catalog)
    {
        byte[] stock = NormalizeContract(catalog.Stock);
        byte[] ownership = NormalizeContract(catalog.Ownership);
        bool ownershipChanged = !ownership.SequenceEqual(catalog.Ownership);
        bool contractsChanged = ownershipChanged || !stock.SequenceEqual(catalog.Stock);
        if (!contractsChanged && (!catalog.ReleaseIdentityMigrationPending || catalog.Pending is not null))
            return catalog;
        FullSourceReleaseIdentity? release = catalog.Release;
        bool migrationPending = false;
        if (release is not null && (ownershipChanged || catalog.ReleaseIdentityMigrationPending))
        {
            byte[]? contract = Digest(catalog.Ownership) == release.ManifestSha256
                ? ownership
                : FindCommittedReleaseContract(entry, catalog, release);
            if (contract is null)
                migrationPending = true;
            else
                release = NormalizeReleaseIdentity(release, contract);
        }
        if (!contractsChanged && migrationPending)
            return catalog;
        return catalog with
        {
            Stock = stock, Ownership = ownership, Release = release, ReleaseIdentityMigrationPending = migrationPending,
            RecoverySources = WithoutBootConfig(catalog.RecoverySources)
        };
    }

    // Source pins an earlier build took of its stock backup name boot.config when that file differed from stock. No
    // contract lists the file now, so such pins would never match the rebuilt ones.
    private static StockArchiveSources? WithoutBootConfig(StockArchiveSources? sources)
    {
        static bool Listed(StockBaselineFile file) => BootConfigFile.IsExcluded(file.Path);
        static bool ListedRange(string range) => range.StartsWith(BootConfigFile.RELATIVE_PATH + "@", StringComparison.OrdinalIgnoreCase);
        if (sources is null || (!sources.CurrentFiles.Any(Listed) && !sources.JournalRanges.Any(ListedRange)))
            return sources;
        string[] ranges = sources.JournalRanges.Where(range => !ListedRange(range)).ToArray();
        return sources with
        {
            CurrentFiles = sources.CurrentFiles.Where(file => !Listed(file)).ToArray(),
            JournalRanges = ranges,
            // The journal is pinned only when a file was recovered through it, and every such file has ranges. When boot.config
            // was the only one, a new observation reads no journal at all, so the pin goes with it.
            Journal = ranges.Length == 0 ? null : sources.Journal
        };
    }

    // A rolled-back operation becomes LastTransaction without replacing the installed release.
    private byte[]? FindCommittedReleaseContract(Ksp2InstallEntry entry, Catalog catalog, FullSourceReleaseIdentity release)
    {
        if (catalog.LastResult == InstallTransactionResult.Committed && catalog.LastTransaction is { } last &&
            ReadCommittedReleaseContract(entry, last, release) is { } latest)
            return latest;
        foreach (string path in _fs.Directory.EnumerateFiles(CatalogRoot(entry), "*.json"))
        {
            if (Guid.TryParseExact(_fs.Path.GetFileNameWithoutExtension(path), "N", out Guid id) && id != catalog.LastTransaction &&
                ReadCommittedReleaseContract(entry, id, release) is { } contract)
                return contract;
        }
        return null;
    }

    private byte[]? ReadCommittedReleaseContract(Ksp2InstallEntry entry, Guid id, FullSourceReleaseIdentity release)
    {
        try
        {
            Operation operation = Read<Operation>(OperationPath(entry, id));
            if (operation.FormatVersion != FORMAT_VERSION || operation.TransactionId != id || operation.InstallationId != entry.Id ||
                operation.InstallRoot != Root(entry) || operation.Phase != "terminal" || operation.Result != InstallTransactionResult.Committed ||
                operation.Release != release || operation.ReleaseManifest is null ||
                Digest(operation.ReleaseManifest) != release.ManifestSha256)
                return null;
            return NormalizeContract(operation.ReleaseManifest);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FullSourceReleaseIdentity NormalizeReleaseIdentity(FullSourceReleaseIdentity release, byte[] contract)
    {
        string normalized = Digest(contract);
        return release.ManifestSha256 == normalized ? release : release with
        {
            PublishedManifestSha256 = release.ManifestSha256,
            ManifestSha256 = normalized
        };
    }

    private static byte[] NormalizeContract(byte[] bytes) => ReadLegacyContract(bytes).ToJson();

    private static StockBaselineManifest ReadLegacyContract(byte[] bytes)
    {
        StockBaselineManifest manifest = StockBaselineManifest.FromJson(bytes);
        if (!manifest.IsCanonical(bytes))
            throw new InvalidDataException("A catalog manifest is noncanonical. Evidence retained.");
        return manifest;
    }

    private Operation ReadOperation(Ksp2InstallEntry entry, Catalog catalog)
    {
        Guid id = catalog.Pending ?? throw new InvalidDataException("No operation is pending.");
        Operation operation = Read<Operation>(OperationPath(entry, id));
        if (operation.Current is null || operation.Target is null || operation.ReleaseManifest is null ||
            operation.ProtectedRoots is null || operation.ProtectedRoots.Length > 1024 ||
            operation.ProtectedRoots.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Required operation contracts or protected roots are invalid. Evidence retained.");
        if (catalog.LegacyStored is not null)
            operation = NormalizeLegacyOperation(operation);
        if (operation.FormatVersion != FORMAT_VERSION || operation.TransactionId != id ||
            operation.InstallationId != entry.Id || operation.InstallRoot != Root(entry) ||
            operation.SourceRoot != SourceRoot(entry, id) || operation.WorkRoot != WorkRoot(entry, id) ||
            operation.PreviousDigest != Digest(JsonSerializer.SerializeToUtf8Bytes((catalog.LegacyStored ?? catalog) with { Pending = null }, JSON_OPTIONS)) ||
            operation.Phase is not ("preparing" or "ready" or "terminal") ||
            (operation.Phase == "terminal") != (operation.Result is not null))
            throw new InvalidDataException("Pending operation authority is corrupt or conflicts with its catalog. Evidence retained.");
        // Once the engine ran, its own journal of that operation lists boot.config, so this build cannot recover it.
        // A ready operation only qualifies for rollback when no engine evidence exists, which reconciliation checks.
        if (catalog.LegacyStored is not null && operation.Phase == "terminal")
            throw LegacyPendingRefusal(entry);
        if (operation.BootConfigSettings is { } bootConfigSettings)
            ValidateBootConfigSettings(bootConfigSettings);
        _engine.ValidateControlledPath(operation.SourceRoot);
        _engine.ValidateControlledPath(operation.WorkRoot);
        StockBaselineManifest current = Manifest(operation.Current);
        StockBaselineManifest known = Manifest(catalog.Ownership);
        var knownFiles = known.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var knownDirectories = known.Directories.ToHashSet(StringComparer.Ordinal);
        if ((!operation.IsRepair && !operation.Current.SequenceEqual(catalog.Ownership)) ||
            current.Files.Any(file => !knownFiles.Contains(file.Path)) ||
            current.Directories.Any(path => !knownDirectories.Contains(path)))
            throw new InvalidDataException("Observed operation ownership exceeds the catalog's previously known paths.");
        _ = Manifest(operation.Target);
        StockBaselineManifest release = Manifest(operation.ReleaseManifest);
        if (operation.Release is { } identity)
        {
            ValidateIdentity(identity);
            if (identity.ManifestSha256 != Digest(release.ToJson()))
                throw new InvalidDataException("Release identity differs from its authenticated manifest.");
            if (!operation.Target.SequenceEqual(operation.ReleaseManifest))
                throw new InvalidDataException("Release-owned target bytes escaped their authenticated contract.");
        }
        else if (!operation.Target.SequenceEqual(catalog.Stock))
        {
            throw new InvalidDataException("Stock restoration differs from the original contract.");
        }
        if (operation.StockArchiveSource && (!_os.IsWindows() || operation.Release is not null || operation.IsRepair ||
            operation.SparseSourceStaging || !operation.ChangedFileStaging || operation.Archive is null))
            throw new InvalidDataException("Archive-backed staging requires an exact Windows stock restoration.");
        if (operation.MovedFileUndo && (!_os.IsWindows() || !operation.ChangedFileStaging))
            throw new InvalidDataException("Moved-file undo requires Windows changed-file staging.");
        if (operation.OwnedSourceStaging && (!operation.MovedFileUndo || operation.StockArchiveSource || operation.Release is null))
            throw new InvalidDataException("Owned source staging requires a Windows release directory source.");
        if (operation.Archive is { } archive)
        {
            ValidateArchiveReference(entry, archive);
            if (catalog.Archive is { } original && archive != original)
                throw new InvalidDataException("The operation replaced the original immutable archive reference.");
        }
        foreach (string root in operation.ProtectedRoots)
        {
            _engine.ValidateControlledPath(root);
            if (root != CatalogRoot(entry) &&
                _fs.Path.GetDirectoryName(root) != StorageRoot(entry) &&
                !_configuration.Config.Ksp2Installs.Where(other => other.Id != entry.Id).Select(Root).Contains(root))
                throw new InvalidDataException("Protected paths are outside configured installations and controlled storage.");
        }
        // The digest of an earlier build's plan covers contracts that listed boot.config, so it cannot match this build's plan.
        if (operation.Phase != "preparing" && catalog.LegacyStored is null && BuildPlan(entry, catalog, operation).GetDigest() != operation.PlanDigest)
            throw new InvalidDataException("The operation plan digest is invalid. Evidence retained.");
        return operation;
    }

    private static void ValidateBootConfigSettings(Dictionary<string, string> settings)
    {
        if (settings.Count > BootConfigFile.MAX_SETTINGS || settings.Any(setting => !BootConfigFile.IsValidSetting(setting.Key, setting.Value)))
            throw new InvalidDataException("The recorded release boot configuration settings are invalid. Evidence retained.");
    }

    // The contracts of an operation written before boot.config left them, in their normalized form.
    private static Operation NormalizeLegacyOperation(Operation operation)
    {
        StockBaselineManifest releaseContract = ReadLegacyContract(operation.ReleaseManifest);
        StockBaselineManifest targetContract = ReadLegacyContract(operation.Target);
        byte[] release = releaseContract.ToJson();
        byte[] target = targetContract.ToJson();
        FullSourceReleaseIdentity? identity = operation.Release;
        if (identity is not null && Digest(operation.ReleaseManifest) == identity.ManifestSha256)
            identity = identity with { ManifestSha256 = Digest(release) };
        if (operation.Phase == "ready" && operation.DerivedBootConfig)
        {
            if (identity is null || !IsLegacyDerivedTarget(targetContract, releaseContract))
                throw new InvalidDataException("Legacy derived target differs from its authenticated release outside boot.config.");
            // This ready journal can only roll back after reconciliation proves that the engine never touched the game.
            target = release;
        }
        return operation with
        {
            Current = NormalizeContract(operation.Current), Target = target,
            ReleaseManifest = release, Release = identity
        };
    }

    private static bool IsLegacyDerivedTarget(StockBaselineManifest target, StockBaselineManifest release) =>
        target.DroppedBootConfig is { } boot && release.DroppedBootConfig is { } publishedBoot &&
        boot.UnixMode == publishedBoot.UnixMode &&
        target.BaselineIdentity == $"{release.BaselineIdentity}:boot-v1:{boot.Sha256}" &&
        target.Provenance == "authenticated release with explicit launcher boot configuration v1" &&
        target.HasUnixMetadata == release.HasUnixMetadata &&
        target.Directories.SequenceEqual(release.Directories) &&
        target.UnixDirectoryModes.SequenceEqual(release.UnixDirectoryModes) &&
        target.Files.SequenceEqual(release.Files);

    // An operation that an earlier build left pending and this one cannot roll back leaves the installation as it is.
    // The way out names everything that would block the next start: the engine marker in the game folder survives a
    // storefront reinstall, and the operation storage beside it would otherwise stay for good.
    private InvalidDataException LegacyPendingRefusal(Ksp2InstallEntry entry, Exception? inner = null) => new(
        "An earlier version of the launcher stopped while changing this game, and this version cannot finish or undo " +
        "that change. To start over, restore the original game files: uninstall the game in Steam or Epic and delete " +
        $"what is left of '{Root(entry)}', or replace your portable copy with an original one. Delete the file " +
        $"'{_fs.Path.Combine(Root(entry), ".redux-transaction.active")}' if it is still there, and delete the folders " +
        $"'{StorageRoot(entry)}' and '{CatalogRoot(entry)}'. Then install Redux again. Copy out any mods you want to " +
        "keep first. Your saves are not affected.", inner);

    private InstallTransactionPlan BuildPlan(Ksp2InstallEntry entry, Catalog catalog, Operation operation)
    {
        StockBaselineManifest current = Manifest(operation.Current);
        StockBaselineManifest target = Manifest(operation.Target);
        var targetFiles = target.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var directories = current.Directories.Concat(target.Directories).ToHashSet(StringComparer.Ordinal);
        return new InstallTransactionPlan(operation.TransactionId, Root(entry), SourceRoot(entry, operation.TransactionId),
            WorkRoot(entry, operation.TransactionId), current, target, Manifest(catalog.Stock),
            operation.Archive ?? throw new InvalidDataException("The operation has no verified stock archive reference."),
            catalog.RetainedFiles.Where(targetFiles.Contains),
            catalog.UnownedDirectories.Where(directories.Contains),
            operation.ProtectedRoots, operation.ChangedFileStaging, operation.StockArchiveSource, operation.MovedFileUndo,
            operation.OwnedSourceStaging);
    }

    private T Read<T>(string path)
    {
        _engine.ValidateControlledPath(path);
        using FileSystemStream input = _fs.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MAX_CATALOG_BYTES)
            throw new InvalidDataException("The catalog exceeds the metadata budget. Evidence retained.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        try
        {
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes, JSON_OPTIONS)
                ?? throw new InvalidDataException("The catalog envelope is empty.");
            if (envelope.FormatVersion != FORMAT_VERSION || envelope.Payload is null ||
                envelope.Sha256 != Digest(envelope.Payload) ||
                !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(envelope, JSON_OPTIONS)))
                throw new InvalidDataException("The catalog is corrupt, noncanonical or has an unknown format. Evidence retained.");
            T value = JsonSerializer.Deserialize<T>(envelope.Payload, JSON_OPTIONS)
                ?? throw new InvalidDataException("The catalog payload is empty.");
            if (!envelope.Payload.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value, JSON_OPTIONS)))
                throw new InvalidDataException("The catalog payload is noncanonical. Evidence retained.");
            return value;
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException("The launcher catalog is unreadable. Evidence retained.", failure);
        }
    }

    private void Write<T>(string path, T value, string boundary, T? previous = default) where T : class
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JSON_OPTIONS);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(FORMAT_VERSION, Digest(payload), payload), JSON_OPTIONS);
        if (bytes.Length > MAX_CATALOG_BYTES)
            throw new InvalidDataException("The catalog exceeds the metadata budget.");
        string parent = _fs.Path.GetDirectoryName(path)!;
        _engine.ValidateControlledPath(parent);
        StockArchive.CreateDirectory(_fs, parent);
        _engine.ValidateControlledPath(path);
        string candidate = _fs.Path.Combine(parent, $".candidate-{Guid.NewGuid():N}.json");
        _engine.ValidateControlledPath(candidate);
        Boundary($"{boundary}:open", () =>
        {
            using FileSystemStream stream = _fs.FileStream.New(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Boundary($"{boundary}:write", () => stream.Write(bytes));
            Boundary($"{boundary}:flush", () => stream.Flush(true));
        });
        // Interrupted candidates are retained. Only the fixed published path grants catalog authority.
        _engine.ValidateControlledPath(candidate);
        _engine.ValidateControlledPath(path);
        Boundary($"{boundary}:publish", () =>
        {
            if (previous is null)
            {
                if (Exists(path))
                    throw new InvalidDataException("New catalog storage contains conflicting evidence.");
            }
            else
            {
                RequirePublishedBytes(path, previous);
            }
            StockArchive.PublishFile(_fs, candidate, path, previous is not null);
        });
    }

    private void RequirePublishedBytes<T>(string path, T previous) where T : class
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(previous, JSON_OPTIONS);
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(new Envelope(FORMAT_VERSION, Digest(payload), payload), JSON_OPTIONS);
        using FileSystemStream input = _fs.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != expected.Length || Convert.ToHexString(SHA256.HashData(input)) != Digest(expected))
            throw new InvalidDataException("Catalog publication found changed evidence. All bytes were retained.");
    }

    private FileSystemStream Lock(Ksp2InstallEntry entry)
    {
        EnsureSupported();
        string root = CatalogRoot(entry);
        StockArchive.CreateDirectory(_fs, root);
        _engine.ValidateControlledPath(root);
        string path = _fs.Path.Combine(root, "coordinator.lock");
        _engine.ValidateControlledPath(path);
        FileSystemStream stream = _fs.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length == 0)
            return stream;
        stream.Dispose();
        throw new InvalidDataException("The coordinator lock contains unknown bytes. Evidence retained.");
    }

    private void ValidateArchiveReference(Ksp2InstallEntry entry, StockBaselineFile archive)
    {
        if (archive.Path != _fs.Path.Combine(Root(entry), "uninstall.zip") || archive.Length is <= 0 or > 130L * 1024 * 1024 * 1024 ||
            string.IsNullOrEmpty(archive.Sha256) || archive.Sha256.Length != 64 || !archive.Sha256.All(Uri.IsHexDigit) ||
            archive.Sha256 != archive.Sha256.ToUpperInvariant())
            throw new InvalidDataException("The immutable archive reference conflicts with its configured root.");
    }

    private static StockBaselineManifest Manifest(byte[] bytes)
    {
        StockBaselineManifest manifest = StockBaselineManifest.FromJson(bytes);
        if (!bytes.SequenceEqual(manifest.ToJson()))
            throw new InvalidDataException("A catalog manifest is noncanonical. Evidence retained.");
        return manifest;
    }

    private static void ValidateIdentity(FullSourceReleaseIdentity identity)
    {
        foreach (string text in new[] { identity.ReleaseId, identity.Channel, identity.Version, identity.Authentication })
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Any(char.IsControl))
                throw new InvalidDataException("The independently authenticated release identity is invalid.");
        }
        if (!IsManifestDigest(identity.ManifestSha256) ||
            (identity.PublishedManifestSha256 is { } published && !IsManifestDigest(published)))
            throw new InvalidDataException("The release manifest digest is invalid.");
        string[] version = identity.Version.Split('.');
        if (version.Length != 5 || !Version.TryParse(string.Join('.', version[..4]), out _) || string.IsNullOrWhiteSpace(version[4]))
            throw new InvalidDataException("The authenticated game version requires its four-component version and build identity.");
    }

    private static bool IsManifestDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(Uri.IsHexDigit) && digest == digest.ToUpperInvariant();

    private static void ValidatePaths(string[] paths, bool directories = false)
    {
        if (paths.Length > 100_000 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Ownership exclusions exceed their budget or contain aliases.");
        // Manifest construction shares the canonical Windows relative-path validation.
        _ = new StockBaselineManifest("ownership-exclusions", "launcher retention policy",
            directories ? [] : paths.Select(path => new StockBaselineFile(path, 0, new string('0', 64))),
            directories ? paths : []);
        if (paths.Any(path => path.Contains('\\')))
            throw new InvalidDataException("Ownership exclusions must use canonical manifest paths.");
    }

    private bool Exists(string path)
    {
        try
        {
            _ = _fs.File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private bool Overlaps(string first, string second) => _engine.PathsOverlap(first, second);

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private void Boundary(string name, Action action)
    {
        _boundary?.Invoke(name, false);
        action();
        _boundary?.Invoke(name, true);
    }
}
