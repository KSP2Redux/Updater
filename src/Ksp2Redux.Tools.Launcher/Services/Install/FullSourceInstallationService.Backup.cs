using Ksp2Redux.Tools.Common.Restoration;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourceInstallationService
{
    private string PreservedBackupPath(Ksp2InstallEntry entry, StockBaselineFile original) =>
        _fs.Path.Combine(StorageRoot(entry), $"legacy-{original.Sha256}.zip");

    private string BackupCandidatePath(Ksp2InstallEntry entry, Guid operation) =>
        _fs.Path.Combine(StorageRoot(entry), $"{operation:N}-stock.zip");

    private void RequireBackupPin(StockBaselineFile expected, CancellationToken ct)
    {
        StockBaselineFile actual = ObserveFile(expected.Path, expected.Path, ct);
        if (actual != expected)
            throw new InvalidDataException("Backup bytes differ from their independently saved archive pin. All evidence retained.");
    }

    private void ValidateRecoveryInputPaths(Ksp2InstallEntry entry, StockBaselineManifest stock)
    {
        foreach (string path in stock.Files.Select(file => file.Path).Concat(stock.Directories))
        {
            _engine.ValidateControlledPath(At(Root(entry), path));
        }
    }

    private async Task<StockBaselineFile> PrepareExistingBackupAsync(Ksp2InstallEntry entry, Catalog catalog, CancellationToken ct)
    {
        StockBaselineFile original = catalog.ExistingBackup!;
        Operation operation = ReadOperation(entry, catalog);
        string candidate = BackupCandidatePath(entry, operation.TransactionId);
        string preserved = PreservedBackupPath(entry, original);
        _engine.ValidateControlledPath(candidate);
        _engine.ValidateControlledPath(preserved);
        if (operation.Archive is not null || Exists(preserved))
            return CompletePendingBackupPromotion(entry, catalog, operation).Archive
                ?? throw new InvalidDataException("Existing backup promotion has no complete archive reference.");
        RequireBackupPin(original, ct);
        if (!Exists(candidate))
        {
            ValidateRecoveryInputPaths(entry, Manifest(catalog.Stock));
            if (catalog.RecoverySources is null)
            {
                StockArchiveSources sources = await _archive.InspectAvailableSourcesAsync(original.Path, Root(entry), Manifest(catalog.Stock), ct).ConfigureAwait(false);
                if (sources.Journal is not null)
                    throw new InvalidDataException("Saved legacy preparation has no journal source pins. Original backup and journal retained.");
            }
            _boundary?.Invoke("archive-recovery-create", false);
            await _archive.CreateRecoveredAsync(original.Path, Root(entry), candidate, Manifest(catalog.Stock), ct,
                catalog.RecoverySources).ConfigureAwait(false);
            _boundary?.Invoke("archive-recovery-create", true);
        }
        return CompletePendingBackupPromotion(entry, catalog, operation).Archive
            ?? throw new InvalidDataException("Recovered backup has no complete archive reference.");
    }

    private Operation CompletePendingBackupPromotion(Ksp2InstallEntry entry, Catalog catalog, Operation operation)
    {
        if (catalog.Archive is not null)
            return operation;
        StockBaselineFile original = catalog.ExistingBackup!;
        string candidate = BackupCandidatePath(entry, operation.TransactionId);
        string preserved = PreservedBackupPath(entry, original);
        _engine.ValidateControlledPath(candidate);
        _engine.ValidateControlledPath(preserved);
        bool candidateVerified = false;
        if (operation.Archive is null)
        {
            if (!Exists(candidate))
            {
                RequireBackupPin(original, CancellationToken.None);
                if (Exists(preserved))
                    throw new InvalidDataException("Original backup retention exists without ready promotion authority.");
                return operation;
            }
            RequireBackupPin(original, CancellationToken.None);
            if (Exists(preserved))
                throw new InvalidDataException("Unknown legacy backup retention collision. Original and candidate preserved.");
            StockBaselineFile complete;
            using (FileSystemStream input = _fs.FileStream.New(candidate, FileMode.Open, FileAccess.Read,
                FileShare.Read, OBSERVATION_BUFFER_BYTES, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                _engine.ValidateControlledPath(candidate);
                // Keep verification and pinning on one read-only handle so the hash names the verified archive.
                Task.Run(() => _archive.VerifyAsync(input, Manifest(catalog.Stock))).GetAwaiter().GetResult();
                complete = new StockBaselineFile(original.Path, input.Length, Convert.ToHexString(SHA256.HashData(input)));
            }

            candidateVerified = true;
            Operation previous = operation;
            operation = operation with { Archive = complete };
            Write(OperationPath(entry, operation.TransactionId), operation, "archive-recovery-ready", previous);
        }
        StockBaselineFile expected = operation.Archive!;
        if (Exists(preserved))
        {
            RequireBackupPin(original with { Path = preserved }, CancellationToken.None);
            if (Exists(original.Path))
            {
                RequireBackupPin(expected, CancellationToken.None);
                if (Exists(candidate))
                    throw new InvalidDataException("Published archive conflicts with a remaining promotion candidate.");
                return operation;
            }
        }
        else
        {
            RequireBackupPin(original, CancellationToken.None);
            RequireBackupPin(expected with { Path = candidate }, CancellationToken.None);
            EnsureMutationAllowed(entry);
            Boundary("archive-legacy-preserve", () =>
            {
                _engine.ValidateControlledPath(preserved);
                RequireBackupPin(original, CancellationToken.None);
                if (Exists(preserved))
                    throw new InvalidDataException("Legacy retention storage changed before publication.");
                StockArchive.PublishFile(_fs, original.Path, preserved, false);
            });
        }
        RequireBackupPin(expected with { Path = candidate }, CancellationToken.None);
        if (!candidateVerified)
            Task.Run(() => _archive.VerifyAsync(candidate, Manifest(catalog.Stock))).GetAwaiter().GetResult();
        EnsureMutationAllowed(entry);
        using FileSystemStream originalLease = _fs.FileStream.New(preserved, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (originalLease.Length != original.Length || Convert.ToHexString(SHA256.HashData(originalLease)) != original.Sha256)
            throw new InvalidDataException("Retained original backup changed before complete archive publication.");
        Boundary("archive-recovery-promote", () =>
        {
            _engine.ValidateControlledPath(original.Path);
            RequireBackupPin(expected with { Path = candidate }, CancellationToken.None);
            RequireBackupPin(original with { Path = preserved }, CancellationToken.None);
            if (Exists(original.Path))
                throw new InvalidDataException("Backup publication destination contains unrecognized bytes.");
            StockArchive.PublishFile(_fs, candidate, original.Path, false);
        });
        RequireBackupPin(expected, CancellationToken.None);
        return operation;
    }
}
