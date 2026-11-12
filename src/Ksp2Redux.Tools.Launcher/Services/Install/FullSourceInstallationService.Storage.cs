using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourceInstallationService
{
    // A saved published head remains evidence after later generations replace installation.json.
    private record RetirementAuthority(int FormatVersion, byte[] PublishedHead, string OperationSha256,
        string PlanSha256, byte[] Source, bool EngineStorageWasAbsent);

    /// <inheritdoc />
    public Task<bool> RetireCompletedPayloadsAsync(Guid installationId, CancellationToken ct)
    {
        if (!_os.IsWindows())
            throw new PlatformNotSupportedException("Terminal payload retirement is verified only on Windows.");
        Ksp2InstallEntry entry = Entry(installationId);
        using FileSystemStream lease = Lock(entry);
        EnsureMutationAllowed(entry);
        return Task.FromResult(RetireCompletedPayloadsLocked(entry, ct));
    }

    private bool RetireCompletedPayloadsLocked(Ksp2InstallEntry entry, CancellationToken ct)
    {
        Catalog? catalog = ReadCatalog(entry);
        if (catalog is null)
            return false;
        if (catalog.Pending is not null || Exists(_fs.Path.Combine(Root(entry), ".redux-transaction.active")))
            throw new IOException("Pending transaction authority prevents payload retirement. Evidence retained.");
        if (catalog.LastTransaction is not { } id || catalog.LastResult is not { } result)
            return false;
        Operation operation = Read<Operation>(OperationPath(entry, id));
        if (operation.Phase == "preparing")
            return false;
        if (operation.FormatVersion != FORMAT_VERSION || operation.TransactionId != id ||
            operation.InstallationId != entry.Id || operation.InstallRoot != Root(entry) ||
            operation.SourceRoot != SourceRoot(entry, id) || operation.WorkRoot != WorkRoot(entry, id) ||
            operation.Phase != "terminal" || operation.Result != result || operation.Archive != catalog.Archive ||
            catalog.Archive is null || operation.Current is null || operation.Target is null ||
            operation.ReleaseManifest is null || operation.ProtectedRoots is null)
            throw new InvalidDataException("Published head and terminal operation conflict. Payloads retained.");
        _engine.ValidateControlledPath(operation.SourceRoot);
        _engine.ValidateControlledPath(operation.WorkRoot);
        foreach (Ksp2InstallEntry other in _configuration.Config.Ksp2Installs.Where(other => other.Id != entry.Id))
        {
            if (Overlaps(Root(other), operation.SourceRoot) || Overlaps(Root(other), operation.WorkRoot))
                throw new InvalidDataException("A configured installation now overlaps disposable storage. Payloads retained.");
        }
        StockBaselineManifest target = Manifest(operation.Target);
        StockBaselineManifest release = Manifest(operation.ReleaseManifest);
        if (operation.Release is { } identity)
        {
            ValidateIdentity(identity);
            if (identity.ManifestSha256 != Digest(release.ToJson()) ||
                (!operation.DerivedBootConfig && !operation.Target.SequenceEqual(operation.ReleaseManifest)) ||
                (operation.DerivedBootConfig && (!target.Directories.SequenceEqual(release.Directories) ||
                    target.Files.Count != release.Files.Count || target.Files.All(file => file.Path != BOOT_CONFIG) ||
                    !target.Files.Where(file => file.Path != BOOT_CONFIG).SequenceEqual(release.Files.Where(file => file.Path != BOOT_CONFIG)))))
                throw new InvalidDataException("Terminal release differs from its independently authenticated contract.");
        }
        else if (!operation.Target.SequenceEqual(catalog.Stock) || operation.DerivedBootConfig)
        {
            throw new InvalidDataException("Terminal stock restoration differs from its original contract.");
        }
        if (result == InstallTransactionResult.Committed)
        {
            var retained = catalog.RetainedFiles.ToHashSet(StringComparer.Ordinal);
            var expected = new StockBaselineManifest(target.BaselineIdentity, target.Provenance,
                target.Files.Where(file => !retained.Contains(file.Path)), target.Directories,
                target.HasUnixMetadata ? target.UnixDirectoryModes : null);
            if (!expected.ToJson().SequenceEqual(catalog.Ownership) || catalog.Release != operation.Release)
                throw new InvalidDataException("Terminal generation was not durably published by this head.");
        }
        // A completed receipt grants no further deletion. Later user drift still needs explicit repair.
        if (!Exists(_fs.Path.Combine(operation.WorkRoot, "payload-retirement.complete")))
        {
            StockBaselineManifest published = Manifest(catalog.Ownership);
            StockBaselineManifest observed = result == InstallTransactionResult.Committed ? published : Manifest(operation.Current);
            var knownFiles = published.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            var knownDirectories = published.Directories.ToHashSet(StringComparer.Ordinal);
            if (result == InstallTransactionResult.RolledBack &&
                ((!operation.IsRepair && !operation.Current.SequenceEqual(catalog.Ownership)) ||
                    observed.Files.Any(file => !knownFiles.Contains(file.Path)) ||
                    observed.Directories.Any(path => !knownDirectories.Contains(path))))
                throw new InvalidDataException("Rolled-back observation escaped published ownership.");
            VerifyTree(Root(entry), observed, ct);
        }
        string planPath = _fs.Path.Combine(operation.WorkRoot, "plan.json");
        _engine.ValidateControlledPath(planPath);
        string receipt = _fs.Path.Combine(CatalogRoot(entry), $"{id:N}-retirement.json");
        RetirementAuthority? persisted = Exists(receipt) ? Read<RetirementAuthority>(receipt) : null;
        bool storageWasAbsent = persisted?.EngineStorageWasAbsent ?? !Exists(operation.WorkRoot);
        byte[] planBytes;
        InstallTransactionPlan plan;
        if (storageWasAbsent)
        {
            if (result != InstallTransactionResult.RolledBack)
                throw new InvalidDataException("Committed engine storage is absent. Payloads retained.");
            plan = BuildPlan(entry, catalog, operation);
            planBytes = plan.ToJson();
        }
        else
        {
            using (FileSystemStream input = _fs.FileStream.New(planPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 48 * 1024 * 1024)
                    throw new InvalidDataException("Terminal plan exceeds its metadata budget.");
                planBytes = new byte[checked((int)input.Length)];
                input.ReadExactly(planBytes);
            }
            // Disk metadata supplies only the two original exclusion lists. Every other field is rebuilt
            // from catalog and operation authority, then the entire canonical plan must match exactly.
            using JsonDocument saved = JsonDocument.Parse(planBytes);
            string[] retainedFiles = ReadPlanPaths(saved.RootElement, "retainedUnownedFiles");
            string[] unownedDirectories = ReadPlanPaths(saved.RootElement, "existingUnownedDirectories");
            plan = new InstallTransactionPlan(id, Root(entry), operation.SourceRoot, operation.WorkRoot,
                Manifest(operation.Current), target, Manifest(catalog.Stock), catalog.Archive,
                retainedFiles, unownedDirectories, operation.ProtectedRoots, operation.ChangedFileStaging,
                operation.StockArchiveSource, operation.MovedFileUndo, operation.OwnedSourceStaging);
        }
        if (Digest(planBytes) != operation.PlanDigest)
            throw new InvalidDataException("Terminal plan differs from the independently saved operation pin.");
        if (!planBytes.SequenceEqual(plan.ToJson()))
            throw new InvalidDataException("Terminal plan escaped its independently retained contracts.");
        StockBaselineManifest source = SourceContract(catalog, operation);
        if (catalog.RetentionPolicy == LEGACY_RETENTION)
        {
            Catalog previous = catalog;
            catalog = catalog with { RetentionPolicy = RETENTION };
            Write(HeadPath(entry), catalog, "catalog-retention", previous);
        }
        var authority = new RetirementAuthority(1, JsonSerializer.SerializeToUtf8Bytes(catalog, JSON_OPTIONS),
            Digest(JsonSerializer.SerializeToUtf8Bytes(operation, JSON_OPTIONS)), plan.GetDigest(), source.ToJson(), storageWasAbsent);
        if (persisted is null)
            Write(receipt, authority, "retirement-authority");
        persisted = Read<RetirementAuthority>(receipt);
        if (!JsonSerializer.SerializeToUtf8Bytes(persisted, JSON_OPTIONS)
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(authority, JSON_OPTIONS)))
            throw new InvalidDataException("Retirement authority conflicts with its published head. Evidence retained.");
        // The original archive reference was published only after complete StockArchive verification.
        // The engine rehashes and holds those exact immutable bytes throughout deletion.
        EnsureMutationAllowed(entry);
        _engine.RetireTerminalPayloads(plan, result, source,
            Digest(JsonSerializer.SerializeToUtf8Bytes(authority, JSON_OPTIONS)), ct, storageWasAbsent);
        _log.Info($"Verified terminal payload retirement for installation {entry.Id}, transaction {id}.");
        return true;
    }

    private static string[] ReadPlanPaths(JsonElement root, string property)
    {
        string[] paths = root.GetProperty(property).EnumerateArray().Select(value => value.GetString()
            ?? throw new InvalidDataException("Terminal plan exclusion is null.")).Take(100_001).ToArray();
        ValidatePaths(paths, property == "existingUnownedDirectories");
        return paths;
    }

    private static StockBaselineManifest SourceContract(Catalog catalog, Operation operation)
    {
        StockBaselineManifest target = Manifest(operation.Target);
        if (operation.StockArchiveSource)
            return new StockBaselineManifest(target.BaselineIdentity, target.Provenance, []);
        if (!operation.SparseSourceStaging)
            return target;
        return ChangedSourceContract(target, Manifest(operation.Current), catalog.RetainedFiles, operation.DerivedBootConfig);
    }

    private static StockBaselineManifest ChangedSourceContract(StockBaselineManifest target,
        StockBaselineManifest current, IEnumerable<string> retainedFiles, bool derivedBootConfig)
    {
        var currentFiles = current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var retained = retainedFiles.ToHashSet(StringComparer.Ordinal);
        return new StockBaselineManifest(target.BaselineIdentity, target.Provenance, target.Files.Where(file =>
                (derivedBootConfig && file.Path == BOOT_CONFIG) ||
                (!retained.Contains(file.Path) && currentFiles.GetValueOrDefault(file.Path) != file)),
            target.Directories, target.HasUnixMetadata ? target.UnixDirectoryModes : null);
    }
}
