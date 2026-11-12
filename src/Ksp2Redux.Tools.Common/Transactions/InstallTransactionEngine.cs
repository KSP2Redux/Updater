using System.Buffers;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.Transactions;

/// <summary>
/// Stage, journal and replace owned installation files with restartable rollback.
/// </summary>
/// <remarks>
/// The caller must stop the game and exclude concurrent writers to all participating roots.
/// The installation lock excludes other engine instances, including instances using another work root.
/// Existing reparse points and case aliases are rejected. This is not a hostile-writer sandbox.
/// Undo and journals remain after completion for separately reviewed retirement.
/// </remarks>
public partial class InstallTransactionEngine
{
    private const string LOCK_NAME = ".redux-transaction.lock";
    private const string ACTIVE_NAME = ".redux-transaction.active";
    private const int BUFFER_BYTES = 1024 * 1024;
    private const int HASH_BUFFER_BYTES = 128 * 1024;
    private const int JOURNAL_MAX_BYTES = 4096;
    private const long RESERVE_BYTES = 64L * 1024 * 1024;
    private readonly IFileSystem _fileSystem;
    private readonly Func<string, long?> _availableSpace;
    private readonly Action<string, bool>? _operationBoundary;
    private readonly Action<string> _validateFileStreams;
    private readonly Func<string, string> _pathIdentity;

    /// <summary>
    /// Construct the engine using the launcher's filesystem abstraction.
    /// </summary>
    /// <param name="fileSystem">The filesystem used for every disk operation.</param>
    /// <param name="availableSpace">The free-space provider, or the filesystem drive query.</param>
    /// <param name="operationBoundary">The optional fault observer receiving an operation name and true after completion.</param>
    /// <param name="validateFileStreams">The unnamed-stream validator for a virtual filesystem, or the native Windows validator.</param>
    /// <param name="pathIdentity">The physical path identity for a virtual filesystem, or null for native identities.</param>
    public InstallTransactionEngine(IFileSystem fileSystem, Func<string, long?>? availableSpace = null,
        Action<string, bool>? operationBoundary = null, Action<string>? validateFileStreams = null,
        Func<string, string>? pathIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (fileSystem is RealFileSystem && pathIdentity is not null)
            throw new ArgumentException("Native path identity cannot be overridden.", nameof(pathIdentity));
        _fileSystem = fileSystem;
        _availableSpace = availableSpace ?? AvailableSpace;
        _operationBoundary = operationBoundary;
        _validateFileStreams = validateFileStreams ?? ValidateNativeFileStreams;
        _pathIdentity = pathIdentity ?? (fileSystem is RealFileSystem ? NativePathIdentity : path => path);
    }

    /// <summary>
    /// Validate a canonical controlled local path with the same native identity rules as transactions.
    /// </summary>
    /// <remarks>
    /// Missing descendants are allowed only after validating their existing ancestor. This grants no ownership.
    /// Native Windows validation is required for real filesystems.
    /// </remarks>
    /// <param name="path">The canonical absolute local path.</param>
    public void ValidateControlledPath(string path)
    {
        DiskPath(path);
        Safe(path);
    }

    /// <summary>
    /// Compare controlled roots using their native physical identities for containment.
    /// </summary>
    /// <param name="first">The first canonical local path.</param>
    /// <param name="second">The second canonical local path.</param>
    /// <returns>True if the physical paths overlap, false otherwise.</returns>
    public bool PathsOverlap(string first, string second)
    {
        ValidateControlledPath(first);
        ValidateControlledPath(second);
        return Overlaps(first, second);
    }

    /// <summary>
    /// Apply a fully verified generation without clearing the installation root.
    /// </summary>
    /// <remarks>
    /// Ordinary failures retain a transaction for Recover. Cancellation attempts rollback without
    /// the cancelled token. A rollback failure is attached as TransactionRecoveryFailure.
    /// Archive content is checked against the caller's verified reference and held read-only.
    /// The caller must first verify that reference with StockArchive and its stock contract.
    /// </remarks>
    /// <param name="plan">The independently trusted immutable ownership and backup contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The committed durable result.</returns>
    /// <exception cref="InvalidDataException">Thrown when contracts, paths, ownership or bytes conflict.</exception>
    /// <exception cref="IOException">Thrown when locking, storage, native publication or journaling fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public InstallTransactionResult Apply(InstallTransactionPlan plan, CancellationToken ct = default)
    {
        ValidateRoots(plan, true);
        using FileSystemStream installationLock = AcquireLock(plan);
        if (Exists(Active(plan)) || Exists(plan.TransactionRoot))
            throw new IOException("An active marker or stale transaction exists. Recover it explicitly first.");
        using FileSystemStream archive = OpenVerifiedArchive(plan, ct);
        using StockArchive.PinnedEntries? stockSource = plan.UsesStockArchiveSource ? OpenStockEntries(plan, archive, ct) : null;
        VerifyInitial(plan, ct);
        // Windows copies at most one new file beside its staged blob, then retires that blob.
        long required = checked(plan.Target.Files.Where(file => plan.RequiresTargetBlob(file) && !plan.MovesSourceFile(file)).Sum(file => file.Length) +
            plan.Current.Files.Where(plan.RequiresUndoBlob).Sum(file => file.Length) +
            (IsNativeWindows
                ? plan.Current.Files.Where(plan.RequiresUndoBlob).Concat(plan.Target.Files.Where(plan.RequiresTargetBlob))
                    .Select(file => file.Length).DefaultIfEmpty().Max()
                : plan.Current.Files.Where(plan.RequiresUndoBlob).Select(file => file.Length).DefaultIfEmpty().Max()) +
            2L * plan.ToJson().Length + RESERVE_BYTES);
        long? available = _availableSpace(_fileSystem.Path.GetDirectoryName(plan.TransactionRoot)!);
        if (available is null || available < required)
            throw new IOException($"Insufficient or unknown transaction disk space. Required {required} bytes.");
        ct.ThrowIfCancellationRequested();
        try
        {
            // Intent is published before owned content is created or any active file is mutated.
            WriteNew(Active(plan), Encoding.ASCII.GetBytes(plan.GetDigest()), "write-active");
            Mutate("create-work", () => CreateDirectory(plan.TransactionRoot));
            WriteNew(Work(plan, "plan.json"), plan.ToJson(), "write-plan");
            AppendState(plan, "preparing");
            if (IsNativeWindows)
                WriteNew(Work(plan, WINDOWS_SIBLING_AUTHORITY), WindowsPublicationAuthority(plan, WINDOWS_SIBLING_AUTHORITY), "write-windows-publication-authority");
            foreach (string directory in new[] { "target", "undo", "removed", "rollback" })
            {
                Mutate($"create-{directory}", () => CreateDirectory(Work(plan, directory)));
            }
            Dictionary<string, int>? windowsCurrentIndices = IsNativeWindows
                ? plan.Current.Files.Select((file, index) => (file.Path, Index: index))
                    .ToDictionary(file => file.Path, file => file.Index, StringComparer.Ordinal)
                : null;
            for (int index = 0; index < plan.Target.Files.Count; index++)
            {
                StockBaselineFile file = plan.Target.Files[index];
                if (!plan.RequiresTargetBlob(file))
                    continue;
                if (plan.MovesSourceFile(file))
                {
                    ct.ThrowIfCancellationRequested();
                    string source = At(plan.SourceRoot, file.Path);
                    string staged = Blob(plan, "target", index);
                    Mutate($"stage-{index}:move", () => MoveVerifiedFile(source, staged, file));
                    continue;
                }
                StockBaselineFile? old = plan.CurrentFile(file.Path);
                string? securitySource = old is not null && old != file ? At(plan.InstallRoot, file.Path) : null;
                using Stream input = stockSource is null ? OpenFileSource(At(plan.SourceRoot, file.Path)) : stockSource.Open(file);
                CopyVerified(input, Blob(plan, "target", index), file, ct, $"stage-{index}", securitySource,
                    IsNativeWindows && securitySource is not null ? (plan, windowsCurrentIndices![file.Path]) : null);
            }
            for (int index = 0; index < plan.Current.Files.Count; index++)
            {
                StockBaselineFile file = plan.Current.Files[index];
                if (!plan.RequiresUndoBlob(file))
                    continue;
                string active = At(plan.InstallRoot, file.Path);
                CopyVerified(active, Blob(plan, "undo", index), file, ct, $"undo-{index}", active,
                    IsNativeWindows ? (plan, index) : null);
            }
            VerifyInitial(plan, ct);
            AppendState(plan, "prepared");
            ct.ThrowIfCancellationRequested();
            AppendState(plan, "mutating");
            CreateTargetDirectories(plan, ct);
            ApplyFiles(plan, ct);
            RemoveOldEmptyDirectories(plan);
            VerifyGeneration(plan, true, ct);
            VerifyWindowsReplacementSecurity(plan);
            ct.ThrowIfCancellationRequested();
            AppendState(plan, "committed");
            ClearActive(plan);
            return InstallTransactionResult.Committed;
        }
        catch (OperationCanceledException failure)
        {
            try
            {
                failure.Data["TransactionRecoveryResult"] = RecoverCore(plan);
            }
            catch (Exception recoveryFailure)
            {
                failure.Data["TransactionRecoveryFailure"] = recoveryFailure;
            }
            failure.Data["TransactionRoot"] = plan.TransactionRoot;
            throw;
        }
        catch (Exception failure)
        {
            failure.Data["TransactionRoot"] = plan.TransactionRoot;
            throw;
        }
    }

    /// <summary>
    /// Reconcile a journal against trusted contracts and restore an uncommitted generation.
    /// </summary>
    /// <remarks>
    /// Repeated recovery uses a fresh engine instance and is idempotent. A committed journal
    /// verifies the target without rolling it back. Corrupt journals and external changes
    /// retain all evidence and fail before rollback mutations. A partial staged target needs
    /// verified source bytes to establish its prefix before preparation cleanup.
    /// Archive failure is reported after successful transaction recovery with the terminal
    /// result in exception Data. Use RecoverWithArchiveHealth to receive both outcomes directly.
    /// </remarks>
    /// <param name="plan">The same independently trusted plan used for Apply.</param>
    /// <returns>The committed or restored durable result.</returns>
    /// <exception cref="InvalidDataException">Thrown when evidence is corrupt, ambiguous or externally changed.</exception>
    /// <exception cref="IOException">Thrown when locking or rollback fails, or archive verification fails after recovery.</exception>
    public InstallTransactionResult Recover(InstallTransactionPlan plan)
    {
        var recovery = RecoverWithArchiveHealth(plan);
        if (recovery.ArchiveFailure is null)
            return recovery.Result;
        var failure = new IOException("Transaction recovery completed, but the immutable stock archive is unavailable or changed.",
            recovery.ArchiveFailure);
        failure.Data["TransactionRecoveryResult"] = recovery.Result;
        failure.Data["TransactionArchiveFailure"] = recovery.ArchiveFailure;
        failure.Data["TransactionRoot"] = plan.TransactionRoot;
        throw failure;
    }

    /// <summary>
    /// Recover trusted transaction evidence before checking the immutable archive's availability and bytes.
    /// </summary>
    /// <remarks>
    /// A non-null archive failure prevents future Apply and requires launcher attention even when
    /// the managed generation is restored or committed. Archive bytes are never repaired or removed.
    /// Rollback still requires exact active ownership, valid undo, plan and journal evidence.
    /// </remarks>
    /// <param name="plan">The same independently trusted plan used for Apply.</param>
    /// <returns>The terminal transaction result and a separate archive failure, or null for a verified archive.</returns>
    /// <exception cref="InvalidDataException">Thrown when transaction evidence or writable paths conflict.</exception>
    /// <exception cref="IOException">Thrown when locking or transaction recovery fails.</exception>
    public (InstallTransactionResult Result, Exception? ArchiveFailure) RecoverWithArchiveHealth(InstallTransactionPlan plan)
    {
        ValidateRoots(plan, false);
        using FileSystemStream installationLock = AcquireLock(plan);
        InstallTransactionResult result = RecoverCore(plan);
        try
        {
            using FileSystemStream archive = OpenVerifiedArchive(plan, CancellationToken.None);
            return (result, null);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (result, failure);
        }
    }

    private InstallTransactionResult RecoverCore(InstallTransactionPlan plan)
    {
        if (!EnsureRecoveryPlan(plan))
            return InstallTransactionResult.RolledBack;
        CheckActive(plan);
        RequireBytes(Work(plan, "plan.json"), plan.ToJson(), 48 * 1024 * 1024);
        string state = ReadState(plan);
        string recoveryStart = state == "preparing" ? "aborting" : "rollingback";
        var recovery = ReadJournal(plan, "recovery", recoveryStart);
        if (state is "committed" or "rolledback" && Exists(Work(plan, "recovery")))
            throw new InvalidDataException("A terminal installation journal conflicts with recovery evidence.");
        if (state == "committed")
        {
            RequireWindowsPublicationCandidatesAbsent(plan);
            VerifyGeneration(plan, true, CancellationToken.None);
            VerifyWindowsReplacementSecurity(plan);
            ClearActive(plan);
            return InstallTransactionResult.Committed;
        }
        if (state == "rolledback" || recovery.State == "rolledback")
        {
            RequireWindowsPublicationCandidatesAbsent(plan);
            VerifyGeneration(plan, false, CancellationToken.None);
            VerifyWindowsRollbackSecurity(plan);
            ClearActive(plan);
            return InstallTransactionResult.RolledBack;
        }
        if (state == "preparing")
        {
            using var targetSource = new PreparationTargetSource(this, plan);
            RequireWindowsPublicationCandidatesAbsent(plan, true);
            VerifyInitial(plan, CancellationToken.None);
            ValidatePreparation(plan, targetSource.Open);
            ValidateWindowsSecurityCandidates(plan, true, targetSource.Open);
            if (recovery.State == "aborted")
            {
                RequireWindowsSecurityCandidatesAbsent(plan);
                foreach (string directory in new[] { "target", "undo", "removed", "rollback" })
                {
                    RequireAbsent(Work(plan, directory));
                }
                ClearActive(plan);
                return InstallTransactionResult.RolledBack;
            }
            if (recovery.State == "")
                AppendRecovery(plan, "aborting", recoveryStart);
            CleanupPreparation(plan, targetSource.Open);
            AppendRecovery(plan, "aborted", recoveryStart);
            ClearActive(plan);
            return InstallTransactionResult.RolledBack;
        }
        ValidateRecoveryFiles(plan);
        CleanupWindowsSecurityCandidates(plan, false);
        CleanupWindowsPublicationCandidates(plan);
        bool verifyWindowsUndoSecurity = HasWindowsPublicationAuthority(plan);
        if (recovery.State != "rollingback")
            AppendRecovery(plan, "rollingback", recoveryStart);
        foreach (string directory in plan.Current.Directories.OrderBy(Depth))
        {
            string path = At(plan.InstallRoot, directory);
            Safe(path);
            if (!_fileSystem.Directory.Exists(path))
                Mutate($"restore-directory:{directory}", () =>
                {
                    CreateDirectory(path);
                    SetLinuxMode(path, plan.Current.HasUnixMetadata ? plan.Current.UnixDirectoryModes[directory] : null);
                });
        }
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            StockBaselineFile old = plan.Current.Files[index];
            string active = At(plan.InstallRoot, old.Path);
            if (plan.IsMovedUndo(old))
            {
                if (!Exists(active))
                {
                    string removed = Blob(plan, "removed", index);
                    Mutate($"restore-moved-{index}", () => MoveVerifiedFile(removed, active, old));
                }
                continue;
            }
            string undo = Blob(plan, "undo", index);
            if (Matches(active, old))
            {
                if (!IsNativeWindows || !verifyWindowsUndoSecurity || !plan.RequiresUndoBlob(old))
                    continue;
                if (WindowsFileSecurityMatches(active, ReadWindowsFileSecurity(undo)))
                    continue;
            }
            string candidate = Blob(plan, "rollback", index);
            if (Exists(candidate))
            {
                // Only this fixed owned candidate may be incomplete after an interrupted copy.
                ValidateRollbackCandidate(candidate, undo, old);
                Mutate($"reset-rollback-{index}", () => _fileSystem.File.Delete(candidate));
            }
            CopyVerified(undo, candidate, old, CancellationToken.None, $"rollback-copy-{index}", undo,
                HasWindowsSiblingAuthority(plan) ? (plan, index) : null);
            byte[]? windowsSecurity = IsNativeWindows ? ReadWindowsFileSecurity(undo) : null;
            StockBaselineFile? target = plan.TargetFile(old.Path);
            Mutate($"restore-file-{index}", () =>
            {
                Safe(active);
                if (IsNativeWindows && windowsSecurity is not null)
                    RequireWindowsFileSecurity(candidate, windowsSecurity);
                if (!Exists(active))
                {
                    RequireAbsent(active);
                    StockArchive.PublishFile(_fileSystem, candidate, active, false);
                }
                else
                {
                    if (Matches(active, old))
                        RequireMatch(active, old);
                    else if (target is not null)
                        RequireMatch(active, target);
                    else
                        throw new InvalidDataException("Rollback active file is neither absent nor the verified old generation.");
                    StockArchive.PublishFile(_fileSystem, candidate, active, true);
                }
                if (IsNativeWindows && windowsSecurity is not null)
                    RequireWindowsFileSecurity(active, windowsSecurity);
            });
        }
        foreach (StockBaselineFile file in plan.Target.Files)
        {
            if (plan.CurrentFile(file.Path) is not null || plan.RetainedUnownedFiles.Contains(file.Path))
                continue;
            string active = At(plan.InstallRoot, file.Path);
            if (!Exists(active))
                continue;
            Mutate($"rollback-add:{file.Path}", () =>
            {
                RequireMatch(active, file);
                _fileSystem.File.Delete(active);
                FlushParent(active);
            });
        }
        foreach (string directory in NewDirectories(plan).OrderByDescending(Depth))
        {
            DeleteEmptyDirectory(At(plan.InstallRoot, directory), $"rollback-directory:{directory}");
        }
        VerifyGeneration(plan, false, CancellationToken.None);
        VerifyWindowsRollbackSecurity(plan);
        AppendRecovery(plan, "rolledback", recoveryStart);
        ClearActive(plan);
        return InstallTransactionResult.RolledBack;
    }

    private void ApplyFiles(InstallTransactionPlan plan, CancellationToken ct)
    {
        Dictionary<string, int>? windowsUndoIndices = IsNativeWindows
            ? plan.Current.Files.Select((file, index) => (file.Path, Index: index))
                .ToDictionary(file => file.Path, file => file.Index, StringComparer.Ordinal)
            : null;
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            StockBaselineFile old = plan.Current.Files[index];
            if (plan.TargetFile(old.Path) is not null)
                continue;
            string active = At(plan.InstallRoot, old.Path);
            string removed = Blob(plan, "removed", index);
            Mutate($"remove-{index}", () =>
            {
                if (plan.IsMovedUndo(old))
                {
                    MoveVerifiedFile(active, removed, old);
                    return;
                }
                RequireMatch(active, old);
                RequireAbsent(removed);
                StockArchive.PublishFile(_fileSystem, active, removed, false);
            });
        }
        for (int index = 0; index < plan.Target.Files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            StockBaselineFile target = plan.Target.Files[index];
            if (plan.RetainedUnownedFiles.Contains(target.Path))
                continue;
            StockBaselineFile? old = plan.CurrentFile(target.Path);
            if (old == target)
                continue;
            string active = At(plan.InstallRoot, target.Path);
            string staged = Blob(plan, "target", index);
            Mutate($"install-{index}", () =>
            {
                RequireMatch(staged, target);
                Safe(active);
                if (IsNativeWindows)
                {
                    string? undo = old is null ? null : Blob(plan, "undo", windowsUndoIndices![old.Path]);
                    PublishWindowsManagedFile(plan, staged, active, target, old, undo, index, ct);
                    return;
                }
                if (old is null)
                {
                    RequireAbsent(active);
                    StockArchive.PublishFile(_fileSystem, staged, active, false);
                }
                else
                {
                    RequireMatch(active, old);
                    StockArchive.PublishFile(_fileSystem, staged, active, true);
                }
            });
        }
    }

    private void MoveVerifiedFile(string source, string destination, StockBaselineFile file)
    {
        RequireMatch(source, file);
        RequireAbsent(destination);
        byte[]? security = IsNativeWindows ? ReadWindowsFileSecurity(source) : null;
        StockArchive.PublishFile(_fileSystem, source, destination, false);
        if (IsNativeWindows && security is not null)
            RequireWindowsFileSecurity(destination, security);
    }

    private void VerifyInitial(InstallTransactionPlan plan, CancellationToken ct)
    {
        VerifyGeneration(plan, false, ct);
        foreach (string directory in NewDirectories(plan))
        {
            ct.ThrowIfCancellationRequested();
            RequireAbsent(At(plan.InstallRoot, directory));
        }
    }

    private void VerifyGeneration(InstallTransactionPlan plan, bool target, CancellationToken ct)
    {
        StockBaselineManifest manifest = target ? plan.Target : plan.Current;
        foreach (StockBaselineFile file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            RequireMatch(At(plan.InstallRoot, file.Path), file);
        }
        foreach (string directory in manifest.Directories)
        {
            RequireDirectory(At(plan.InstallRoot, directory));
            RequireLinuxDirectoryMode(At(plan.InstallRoot, directory), manifest, directory);
        }
        foreach (StockBaselineFile file in (target ? plan.Current : plan.Target).Files)
        {
            if ((target ? plan.TargetFile(file.Path) : plan.CurrentFile(file.Path)) is not null ||
                plan.RetainedUnownedFiles.Contains(file.Path))
                continue;
            RequireAbsent(At(plan.InstallRoot, file.Path));
        }
        foreach (string path in plan.RetainedUnownedFiles)
        {
            RequireMatch(At(plan.InstallRoot, path), plan.TargetFile(path)!);
        }
        foreach (string directory in plan.ExistingUnownedDirectories)
        {
            RequireDirectory(At(plan.InstallRoot, directory));
        }
    }

    private void ValidateRecoveryFiles(InstallTransactionPlan plan)
    {
        ValidateWindowsPublicationCandidates(plan);
        ValidateWindowsSecurityCandidates(plan, false);
        // Validate the entire rollback set before the first active mutation, preserving conflicts as evidence.
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            StockBaselineFile old = plan.Current.Files[index];
            if (plan.IsMovedUndo(old))
            {
                string original = At(plan.InstallRoot, old.Path);
                string removed = Blob(plan, "removed", index);
                // A same-volume rename leaves the original in exactly one of these locations.
                RequireAbsent(Blob(plan, "undo", index));
                RequireAbsent(Blob(plan, "rollback", index));
                if (Exists(original))
                {
                    RequireMatch(original, old);
                    RequireAbsent(removed);
                }
                else
                    RequireMatch(removed, old);
                continue;
            }
            if (!plan.RequiresUndoBlob(old))
            {
                RequireMatch(At(plan.InstallRoot, old.Path), old);
                RequireAbsent(Blob(plan, "undo", index));
                RequireAbsent(Blob(plan, "rollback", index));
                RequireAbsent(Blob(plan, "removed", index));
                continue;
            }
            RequireMatch(Blob(plan, "undo", index), old);
            ValidateRollbackCandidate(Blob(plan, "rollback", index), Blob(plan, "undo", index), old);
            string active = At(plan.InstallRoot, old.Path);
            StockBaselineFile? target = plan.TargetFile(old.Path);
            if (Matches(active, old) || (target is not null && Matches(active, target)))
                continue;
            // Earlier ReplaceFileW publication could fail after the changed active name disappeared.
            // Exact undo and trusted changed-path ownership remain mandatory before restoration.
            if (target is not null && target != old && !Exists(active))
                continue;
            if (target is null && !Exists(active) && Matches(Blob(plan, "removed", index), old))
                continue;
            throw new InvalidDataException($"External change or missing removal evidence at '{old.Path}'.");
        }
        foreach (StockBaselineFile target in plan.Target.Files)
        {
            if (plan.CurrentFile(target.Path) is not null)
                continue;
            string active = At(plan.InstallRoot, target.Path);
            if ((plan.RetainedUnownedFiles.Contains(target.Path) || Exists(active)) && !Matches(active, target))
                throw new InvalidDataException($"External change at '{target.Path}'.");
        }
        if (plan.UsesChangedFileStaging)
        {
            for (int index = 0; index < plan.Target.Files.Count; index++)
            {
                if (!plan.RequiresTargetBlob(plan.Target.Files[index]))
                    RequireAbsent(Blob(plan, "target", index));
            }
        }
        foreach (string directory in plan.Current.Directories.Concat(plan.Target.Directories))
        {
            Safe(At(plan.InstallRoot, directory));
            if (_fileSystem.File.Exists(At(plan.InstallRoot, directory)))
                throw new InvalidDataException($"External file/directory transition at '{directory}'.");
        }
        foreach (string directory in plan.ExistingUnownedDirectories)
        {
            RequireDirectory(At(plan.InstallRoot, directory));
        }
    }

    private void CreateTargetDirectories(InstallTransactionPlan plan, CancellationToken ct)
    {
        foreach (string directory in NewDirectories(plan).OrderBy(Depth))
        {
            ct.ThrowIfCancellationRequested();
            string path = At(plan.InstallRoot, directory);
            Mutate($"create-directory:{directory}", () =>
            {
                RequireAbsent(path);
                CreateDirectory(path);
                SetLinuxMode(path, plan.Target.HasUnixMetadata ? plan.Target.UnixDirectoryModes[directory] : null);
            });
        }
    }

    private void RemoveOldEmptyDirectories(InstallTransactionPlan plan)
    {
        foreach (string directory in plan.Current.Directories.Except(plan.Target.Directories)
                     .Except(plan.ExistingUnownedDirectories).OrderByDescending(Depth))
        {
            DeleteEmptyDirectory(At(plan.InstallRoot, directory), $"remove-directory:{directory}");
        }
    }

    private void DeleteEmptyDirectory(string path, string operation)
    {
        Safe(path);
        if (!_fileSystem.Directory.Exists(path))
            return;
        if (_fileSystem.Directory.EnumerateFileSystemEntries(path).Any())
            return;
        Mutate(operation, () =>
        {
            Safe(path);
            _fileSystem.Directory.Delete(path, false);
            FlushParent(path);
        });
    }

    private static IEnumerable<string> NewDirectories(InstallTransactionPlan plan)
        => plan.Target.Directories.Except(plan.Current.Directories).Except(plan.ExistingUnownedDirectories);

    private static int Depth(string path) => path.Count(character => character == '/');

    private void ValidateRoots(InstallTransactionPlan plan, bool requireSource)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = plan.ToJson();
        string[] roots = [plan.InstallRoot, plan.SourceRoot, plan.TransactionRoot];
        foreach (string root in roots.Concat(plan.ProtectedRoots))
        {
            DiskPath(root);
            Safe(root);
        }
        DiskPath(plan.Archive.Path);
        if (requireSource)
            Safe(plan.Archive.Path);
        if (Overlaps(roots[0], roots[1]) || Overlaps(roots[0], roots[2]) || Overlaps(roots[1], roots[2]) ||
            Overlaps(roots[2], plan.Archive.Path) || Overlaps(roots[1], plan.Archive.Path))
            throw new InvalidDataException("Installation, source, transaction and archive roots conflict.");
        foreach (string root in plan.ProtectedRoots)
        {
            if (Overlaps(plan.InstallRoot, root) || Overlaps(plan.TransactionRoot, root) ||
                (plan.UsesOwnedSourceStaging && Overlaps(plan.SourceRoot, root)))
                throw new InvalidDataException("A writable root overlaps a protected path.");
        }
        if (_fileSystem.Path.GetPathRoot(_pathIdentity(plan.InstallRoot)) != _fileSystem.Path.GetPathRoot(_pathIdentity(plan.TransactionRoot)))
            throw new InvalidDataException("Transaction storage must use the installation volume.");
        RequireDirectory(plan.InstallRoot);
        RequireDirectory(_fileSystem.Path.GetDirectoryName(plan.TransactionRoot)!);
        ValidateLinuxRoots(plan);
        if (plan.UsesStockArchiveSource && _fileSystem is RealFileSystem && !IsNativeWindows)
            throw new InvalidDataException("Archive-backed stock staging is supported on Windows only.");
        if (plan.UsesMovedFileUndo && _fileSystem is RealFileSystem && !IsNativeWindows)
            throw new InvalidDataException("Moved-file undo is supported on Windows only.");
        if (plan.UsesOwnedSourceStaging && (!IsNativeWindows ||
            _fileSystem.Path.GetDirectoryName(plan.SourceRoot) != _fileSystem.Path.GetDirectoryName(plan.TransactionRoot)))
            throw new InvalidDataException("Owned source staging requires native Windows sibling storage.");
        if (IsNativeWindows && (requireSource || Exists(Work(plan, WINDOWS_PUBLICATION_AUTHORITY)) || Exists(Work(plan, WINDOWS_SIBLING_AUTHORITY))))
        {
            if (!requireSource)
                _ = HasWindowsPublicationAuthority(plan, true);
            ValidateWindowsPublicationPaths(plan, requireSource);
        }
        if (requireSource)
        {
            RequireDirectory(plan.SourceRoot);
            if (plan.UsesStockArchiveSource && _fileSystem.Directory.EnumerateFileSystemEntries(plan.SourceRoot).Any())
                throw new InvalidDataException("Archive-backed staging requires an empty owned source directory.");
        }
        if (Overlaps(plan.InstallRoot, plan.Archive.Path) &&
            !_pathIdentity(plan.Archive.Path).Equals(_pathIdentity(plan.InstallRoot) + _fileSystem.Path.DirectorySeparatorChar + "uninstall.zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only immutable uninstall.zip may occupy the installation root.");
    }

    private FileSystemStream AcquireLock(InstallTransactionPlan plan)
    {
        string path = At(plan.InstallRoot, LOCK_NAME);
        Safe(path);
        // Never truncate a preexisting lock. Nonempty user files are a reserved-name conflict.
        FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length != 0)
        {
            stream.Dispose();
            throw new InvalidDataException("The installation lock path contains unknown bytes.");
        }
        return stream;
    }

    private FileSystemStream OpenVerifiedArchive(InstallTransactionPlan plan, CancellationToken ct)
    {
        Safe(plan.Archive.Path);
        FileSystemStream stream = _fileSystem.FileStream.New(plan.Archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            VerifyStream(stream, null, plan.Archive, ct);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private StockArchive.PinnedEntries OpenStockEntries(InstallTransactionPlan plan, Stream archive, CancellationToken ct) =>
        new StockArchive(_fileSystem, new ZipFileService(_fileSystem)).OpenPinnedEntries(archive, plan.Stock, ct);

    private FileSystemStream OpenFileSource(string source)
    {
        Safe(source);
        return _fileSystem.FileStream.New(source, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private void CopyVerified(string source, string destination, StockBaselineFile file, CancellationToken ct, string name,
        string? windowsSecuritySource = null, (InstallTransactionPlan Plan, int Index)? windowsSibling = null)
    {
        using FileSystemStream input = OpenFileSource(source);
        CopyVerified(input, destination, file, ct, name, windowsSecuritySource, windowsSibling);
    }

    private void CopyVerified(Stream input, string destination, StockBaselineFile file, CancellationToken ct, string name,
        string? windowsSecuritySource = null, (InstallTransactionPlan Plan, int Index)? windowsSibling = null)
    {
        if (IsNativeWindows && windowsSecuritySource is not null)
        {
            CopyWindowsSecurityVerified(input, destination, file, ct, name, windowsSecuritySource, windowsSibling);
            return;
        }
        Safe(destination);
        Mutate($"{name}:open", () =>
        {
            using FileSystemStream output = _fileSystem.FileStream.New(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            VerifyStream(input, output, file, ct, name);
            Mutate($"{name}:flush", () => output.Flush(true));
        });
        SetLinuxMode(destination, file.UnixMode);
        FlushParent(destination);
        RequireMatch(destination, file);
    }

    private void ValidateRollbackCandidate(string candidate, string undo, StockBaselineFile old)
    {
        Safe(candidate);
        if (!Exists(candidate))
            return;
        using FileSystemStream original = OpenFileSource(undo);
        ValidateRollbackCandidate(candidate, original, old);
    }

    private void ValidateRollbackCandidate(string candidate, Stream original, StockBaselineFile old)
    {
        Safe(candidate);
        if (!Exists(candidate))
            return;
        using FileSystemStream partial = _fileSystem.FileStream.New(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (partial.Length > old.Length)
            throw new InvalidDataException("Rollback candidate exceeds the verified original length.");
        byte[] candidateBytes = new byte[BUFFER_BYTES];
        byte[] originalBytes = new byte[BUFFER_BYTES];
        long remaining = partial.Length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(remaining, BUFFER_BYTES);
            partial.ReadExactly(candidateBytes.AsSpan(0, count));
            original.ReadExactly(originalBytes.AsSpan(0, count));
            if (!candidateBytes.AsSpan(0, count).SequenceEqual(originalBytes.AsSpan(0, count)))
                throw new InvalidDataException("Rollback candidate is not an interrupted prefix of verified undo. Evidence retained.");
            remaining -= count;
        }
    }

    private void VerifyStream(Stream input, Stream? output, StockBaselineFile file, CancellationToken ct, string name = "verify")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_BYTES);
        try
        {
            long remaining = file.Length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, (int)Math.Min(remaining, BUFFER_BYTES));
                if (read == 0)
                    throw new InvalidDataException($"Truncated content at '{file.Path}'.");
                hash.AppendData(buffer, 0, read);
                if (output is not null)
                    Mutate($"{name}:write", () => output.Write(buffer, 0, read));
                remaining -= read;
            }
            if (input.ReadByte() != -1 || Convert.ToHexString(hash.GetHashAndReset()) != file.Sha256)
                throw new InvalidDataException($"Unexpected bytes at '{file.Path}'.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool Matches(string path, StockBaselineFile file)
    {
        Safe(path);
        if (!_fileSystem.File.Exists(path))
            return false;
        // Buffer the hash reader's small reads without allocating a large buffer for small files.
        int bufferSize = (int)Math.Clamp(file.Length, 4096, HASH_BUFFER_BYTES);
        using FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.SequentialScan);
        if (stream.Length != file.Length)
            return false;
        return Convert.ToHexString(SHA256.HashData(stream)) == file.Sha256 &&
            (!IsNativeLinux || file.UnixMode is null || StockArchive.LinuxModeMatches(path, file.UnixMode.Value));
    }

    private void RequireMatch(string path, StockBaselineFile file)
    {
        if (!Matches(path, file))
            throw new InvalidDataException($"Expected bytes are absent or changed at '{path}'.");
    }

    private void RequireAbsent(string path)
    {
        Safe(path);
        if (Exists(path))
            throw new InvalidDataException($"An unowned or conflicting path already exists: '{path}'.");
    }

    private void RequireDirectory(string path)
    {
        Safe(path);
        if (!_fileSystem.Directory.Exists(path))
            throw new InvalidDataException($"Expected directory is absent: '{path}'.");
    }

    private bool Exists(string path) => _fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path);

    private void WriteNew(string path, byte[] bytes, string name)
    {
        Safe(path);
        Mutate($"{name}:open", () =>
        {
            using FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Mutate($"{name}:write", () => stream.Write(bytes));
            Mutate($"{name}:flush", () => stream.Flush(true));
        });
        FlushParent(path);
    }

    private void AppendState(InstallTransactionPlan plan, string state)
    {
        string path = Work(plan, "journal");
        var journal = ReadJournal(plan, "journal", "preparing");
        if (journal.Bytes.Length != journal.CompleteLength ||
            (journal.CompleteLength == 0 ? state != "preparing" : !AllowedTransition(journal.State, state)))
            throw new InvalidDataException($"Invalid or incomplete journal transition to '{state}'.");
        byte[] bytes = StateRecord(plan, state);
        Safe(path);
        Mutate($"journal-{state}:open", () =>
        {
            using FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            if (stream.Length + bytes.Length > JOURNAL_MAX_BYTES)
                throw new InvalidDataException("Journal byte budget exceeded.");
            stream.Seek(0, SeekOrigin.End);
            Mutate($"journal-{state}:write", () => stream.Write(bytes));
            Mutate($"journal-{state}:flush", () => stream.Flush(true));
        });
        FlushParent(path);
    }

    private string ReadState(InstallTransactionPlan plan)
        => ReadJournal(plan, "journal", "preparing").State;

    private static byte[] StateRecord(InstallTransactionPlan plan, string state)
    {
        string payload = $"1|{plan.GetDigest()}|{state}";
        return Encoding.ASCII.GetBytes($"{payload}|{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(payload)))}\n");
    }

    private (string State, byte[] Bytes, int CompleteLength) ReadJournal(InstallTransactionPlan plan,
        string name, string firstState)
    {
        string path = Work(plan, name);
        Safe(path);
        if (Exists(path) && !_fileSystem.File.Exists(path))
            throw new InvalidDataException("A journal path is not a regular file.");
        byte[] bytes = _fileSystem.File.Exists(path) ? ReadBounded(path, JOURNAL_MAX_BYTES) : [];
        string previous = name == "journal" ? "preparing" : "";
        int offset = 0;
        while (offset < bytes.Length)
        {
            string[] nextStates = offset == 0 ? [firstState] : NextStates(previous, name == "recovery");
            int newline = Array.IndexOf(bytes, (byte)'\n', offset);
            int length = newline < 0 ? bytes.Length - offset : newline - offset + 1;
            string? matched = null;
            foreach (string next in nextStates)
            {
                byte[] expected = StateRecord(plan, next);
                if ((newline < 0 ? length < expected.Length : length == expected.Length) &&
                    bytes.AsSpan(offset, length).SequenceEqual(expected.AsSpan(0, length)))
                {
                    matched = next;
                    break;
                }
            }
            if (matched is null)
                throw new InvalidDataException("Corrupt, foreign or illegally ordered journal record. Evidence retained.");
            if (newline < 0)
                return (previous, bytes, offset);
            previous = matched;
            offset += length;
        }
        return (previous, bytes, offset);
    }

    private static string[] NextStates(string state, bool recovery)
        => recovery ? state switch
        {
            "aborting" => ["aborted"],
            "rollingback" => ["rolledback"],
            _ => []
        } : state switch
        {
            "preparing" => ["preparing", "prepared"],
            "prepared" => ["mutating", "rollingback"],
            "mutating" => ["committed", "rollingback"],
            "rollingback" => ["rolledback"],
            _ => []
        };

    private void AppendRecovery(InstallTransactionPlan plan, string state, string firstState)
    {
        var journal = ReadJournal(plan, "recovery", firstState);
        if (journal.State == state && journal.CompleteLength == journal.Bytes.Length)
            return;
        if (!(journal.CompleteLength == 0 ? state == firstState : NextStates(journal.State, true).Contains(state)))
            throw new InvalidDataException("Invalid recovery journal transition.");
        byte[] record = StateRecord(plan, state);
        int partialLength = journal.Bytes.Length - journal.CompleteLength;
        if (partialLength >= record.Length ||
            !journal.Bytes.AsSpan(journal.CompleteLength).SequenceEqual(record.AsSpan(0, partialLength)))
            throw new InvalidDataException("Incomplete recovery record conflicts with the verified outcome.");
        // Complete only the exact own prefix. The original installation journal is never rewritten.
        AppendExpected(Work(plan, "recovery"), journal.Bytes, record[partialLength..], $"recovery-{state}", JOURNAL_MAX_BYTES);
    }

    private void AppendExpected(string path, byte[] previous, byte[] suffix, string name, int limit)
    {
        Safe(path);
        Mutate($"{name}:open", () =>
        {
            using FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length != previous.Length || stream.Length + suffix.Length > limit)
                throw new InvalidDataException("Publication length differs from verified evidence.");
            byte[] actual = new byte[previous.Length];
            stream.ReadExactly(actual);
            if (!actual.SequenceEqual(previous))
                throw new InvalidDataException("Publication changed after reconciliation.");
            Mutate($"{name}:write", () => stream.Write(suffix));
            Mutate($"{name}:flush", () => stream.Flush(true));
        });
        FlushParent(path);
    }

    private static bool AllowedTransition(string old, string next)
        => (old == "preparing" && next is "preparing" or "prepared") ||
           (old == "prepared" && next is "mutating" or "rollingback") ||
           (old == "mutating" && next is "committed" or "rollingback") ||
           (old == "rollingback" && next == "rolledback");

    private void CheckActive(InstallTransactionPlan plan)
    {
        string path = Active(plan);
        Safe(path);
        if (Exists(path))
            RequireBytes(path, Encoding.ASCII.GetBytes(plan.GetDigest()), 64);
        else if (ReadState(plan) is "prepared" or "mutating" or "rollingback" &&
                 ReadJournal(plan, "recovery", "rollingback").State != "rolledback")
            throw new InvalidDataException("Active transaction marker is missing. Recovery authority is ambiguous.");
    }

    private void ClearActive(InstallTransactionPlan plan)
    {
        CheckActive(plan);
        if (Exists(Active(plan)))
            Mutate("clear-active", () =>
            {
                _fileSystem.File.Delete(Active(plan));
                FlushParent(Active(plan));
            });
    }

    private bool EnsureRecoveryPlan(InstallTransactionPlan plan)
    {
        byte[] expectedPlan = plan.ToJson();
        byte[] expectedMarker = Encoding.ASCII.GetBytes(plan.GetDigest());
        if (!Exists(plan.TransactionRoot))
        {
            VerifyInitial(plan, CancellationToken.None);
            if (!Exists(Active(plan)))
                return false;
            byte[] marker = ReadBounded(Active(plan), expectedMarker.Length);
            RequirePrefix(marker, expectedMarker);
            AppendExpected(Active(plan), marker, expectedMarker[marker.Length..], "complete-active", expectedMarker.Length);
            Mutate("create-work", () => CreateDirectory(plan.TransactionRoot));
        }
        RequireDirectory(plan.TransactionRoot);
        string path = Work(plan, "plan.json");
        Safe(path);
        byte[] actual = _fileSystem.File.Exists(path) ? ReadBounded(path, 48 * 1024 * 1024) : [];
        if (actual.SequenceEqual(expectedPlan))
        {
            // The earlier protocol wrote the full plan before opening the marker.
            if (Exists(Active(plan)))
            {
                byte[] marker = ReadBounded(Active(plan), expectedMarker.Length);
                if (!marker.SequenceEqual(expectedMarker))
                {
                    RequirePrefix(marker, expectedMarker);
                    RequireInitializationEntries(plan);
                    VerifyInitial(plan, CancellationToken.None);
                    AppendExpected(Active(plan), marker, expectedMarker[marker.Length..], "complete-active", expectedMarker.Length);
                }
            }
            return true;
        }
        // A full reservation binds this otherwise incomplete tree to the trusted caller plan.
        RequireBytes(Active(plan), expectedMarker, expectedMarker.Length);
        RequireInitializationEntries(plan);
        RequirePrefix(actual, expectedPlan);
        VerifyInitial(plan, CancellationToken.None);
        AppendExpected(path, actual, expectedPlan[actual.Length..], "complete-plan", 48 * 1024 * 1024);
        return true;
    }

    private void RequireInitializationEntries(InstallTransactionPlan plan)
    {
        foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(plan.TransactionRoot))
        {
            Safe(entry);
            if (entry != Work(plan, "plan.json") || !_fileSystem.File.Exists(entry))
                throw new InvalidDataException("Incomplete initialization contains unrelated work. Evidence retained.");
        }
    }

    private static void RequirePrefix(byte[] actual, byte[] expected)
    {
        if (actual.Length > expected.Length || !actual.AsSpan().SequenceEqual(expected.AsSpan(0, actual.Length)))
            throw new InvalidDataException("Incomplete publication is not an exact prefix of trusted intent.");
    }

    private sealed class PreparationTargetSource(InstallTransactionEngine engine, InstallTransactionPlan plan) : IDisposable
    {
        private FileSystemStream? _archive;
        private StockArchive.PinnedEntries? _entries;

        internal Stream Open(StockBaselineFile file)
        {
            if (!plan.UsesStockArchiveSource)
            {
                string path = engine.At(plan.SourceRoot, file.Path);
                engine.RequireMatch(path, file);
                return engine.OpenFileSource(path);
            }
            // Preparation recovery needs archive bytes only when a partial target prefix exists.
            _archive ??= engine.OpenVerifiedArchive(plan, CancellationToken.None);
            _entries ??= engine.OpenStockEntries(plan, _archive, CancellationToken.None);
            return _entries.Open(file);
        }

        public void Dispose()
        {
            _entries?.Dispose();
            _archive?.Dispose();
        }
    }

    private void ValidatePreparation(InstallTransactionPlan plan, Func<StockBaselineFile, Stream> openTarget)
    {
        // Never recursively delete a caller-controlled tree. Only fixed owned names are eligible.
        foreach (string directory in new[] { "target", "undo", "removed", "rollback" })
        {
            string path = Work(plan, directory);
            Safe(path);
            if (!_fileSystem.Directory.Exists(path))
                continue;
            int count = directory switch
            {
                "target" => plan.Target.Files.Count,
                "undo" => plan.Current.Files.Count,
                _ => 0
            };
            var allowed = Enumerable.Range(0, count).Where(index => directory == "target"
                    ? plan.RequiresTargetBlob(plan.Target.Files[index]) : plan.RequiresUndoBlob(plan.Current.Files[index]))
                .ToDictionary(index => Blob(plan, directory, index),
                index => index, StringComparer.Ordinal);
            foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(path))
            {
                Safe(entry);
                if (!allowed.TryGetValue(entry, out int index) || !_fileSystem.File.Exists(entry))
                    throw new InvalidDataException("Unknown preparation data retained.");
                StockBaselineFile file = directory == "target" ? plan.Target.Files[index] : plan.Current.Files[index];
                if (directory == "target" && plan.MovesSourceFile(file))
                {
                    RequireAbsent(At(plan.SourceRoot, file.Path));
                    RequireMatch(entry, file);
                    continue;
                }
                if (Matches(entry, file))
                    continue;
                if (directory == "target")
                {
                    using Stream original = openTarget(file);
                    ValidateRollbackCandidate(entry, original, file);
                }
                else
                {
                    string original = At(plan.InstallRoot, file.Path);
                    RequireMatch(original, file);
                    ValidateRollbackCandidate(entry, original, file);
                }
            }
        }
        foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(plan.TransactionRoot))
        {
            Safe(entry);
            string name = _fileSystem.Path.GetFileName(entry);
            bool directory = new[] { "target", "undo", "removed", "rollback" }.Contains(name, StringComparer.Ordinal);
            bool metadata = new[] { "plan.json", "journal", "recovery" }.Contains(name, StringComparer.Ordinal) ||
                (IsNativeWindows && name is WINDOWS_PUBLICATION_AUTHORITY or WINDOWS_SIBLING_AUTHORITY);
            if (directory ? !_fileSystem.Directory.Exists(entry) : !metadata || !_fileSystem.File.Exists(entry))
                throw new InvalidDataException("Unknown transaction data retained.");
        }
    }

    private void CleanupPreparation(InstallTransactionPlan plan, Func<StockBaselineFile, Stream> openTarget)
    {
        CleanupWindowsSecurityCandidates(plan, true, openTarget);
        for (int index = 0; index < plan.Target.Files.Count; index++)
        {
            StockBaselineFile file = plan.Target.Files[index];
            if (!plan.MovesSourceFile(file))
                continue;
            string staged = Blob(plan, "target", index);
            if (Exists(staged))
                Mutate($"cleanup-moved-stage-{index}", () => MoveVerifiedFile(staged, At(plan.SourceRoot, file.Path), file));
        }
        foreach (string directory in new[] { "target", "undo", "removed", "rollback" })
        {
            string path = Work(plan, directory);
            if (!_fileSystem.Directory.Exists(path))
                continue;
            foreach (string file in _fileSystem.Directory.EnumerateFiles(path).ToArray())
            {
                Mutate("cleanup-prepared-file", () => _fileSystem.File.Delete(file));
            }
            Mutate("cleanup-prepared-directory", () => _fileSystem.Directory.Delete(path, false));
        }
        // Plan and both journals remain as ownership evidence for every later recovery call.
    }

    private void RequireBytes(string path, byte[] expected, int limit)
    {
        Safe(path);
        if (!_fileSystem.File.Exists(path) || !ReadBounded(path, limit).SequenceEqual(expected))
            throw new InvalidDataException("Disk intent differs from the independently supplied plan.");
    }

    private byte[] ReadBounded(string path, int limit)
    {
        using FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit)
            throw new InvalidDataException("Metadata byte budget exceeded.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private string DiskPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_fileSystem.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Only absolute local-volume disk paths are supported.");
        string full = _fileSystem.Path.TrimEndingDirectorySeparator(_fileSystem.Path.GetFullPath(path));
        if (full != path || path.Length > 32760)
            throw new InvalidDataException("Disk paths must be canonical, without traversal or trailing separators.");
        string root = _fileSystem.Path.GetPathRoot(full)!;
        if (full == _fileSystem.Path.TrimEndingDirectorySeparator(root))
            throw new InvalidDataException("A volume root cannot be an installation or transaction path.");
        foreach (string segment in full[root.Length..].Split(_fileSystem.Path.DirectorySeparatorChar))
        {
            StockBaselineManifest.ValidateSegment(segment);
        }
        return full;
    }

    private void Safe(string path)
    {
        string root = _fileSystem.Path.GetPathRoot(path)!;
        string current = root;
        if ((_fileSystem.File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reparse volume root is invalid.");
        foreach (string segment in path[root.Length..].Split(_fileSystem.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_fileSystem.Directory.Exists(current))
                throw new InvalidDataException($"A path ancestor is a file: '{current}'.");
            string[] matches = _fileSystem.Directory.EnumerateFileSystemEntries(current)
                .Where(entry => _fileSystem.Path.GetFileName(entry).Equals(segment, StringComparison.OrdinalIgnoreCase)).ToArray();
            string next = _fileSystem.Path.Combine(current, segment);
            if (matches.Length > 1 || (matches.Length == 1 && matches[0] != next))
                throw new InvalidDataException($"Case alias or collision at '{next}'.");
            if (matches.Length == 0)
            {
                try
                {
                    _ = _fileSystem.File.GetAttributes(next);
                    throw new InvalidDataException($"An existing path is not explained by its enumerated name: '{next}'.");
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
                if (_fileSystem is RealFileSystem)
                    ValidateNativePathIdentity(current);
                return;
            }
            if ((_fileSystem.File.GetAttributes(next) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse point at '{next}'.");
            // Ancestor streams outside participating roots are never changed or deleted.
            // Every managed directory is also validated as a leaf through its own contract entry.
            if (next == path)
            {
                if (_fileSystem is RealFileSystem)
                    ValidateNativePathIdentity(next);
                _validateFileStreams(next);
            }
            current = next;
        }
    }

    private string NativePathIdentity(string path) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? WindowsPhysicalPathIdentity(path) : path;

    private bool Overlaps(string first, string second)
    {
        if (IsNativeLinux)
            return LinuxOverlaps(first, second);
        first = _pathIdentity(first);
        second = _pathIdentity(second);
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith(second + _fileSystem.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + _fileSystem.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string At(string root, string relative)
        => _fileSystem.Path.Combine(root, relative.Replace('/', _fileSystem.Path.DirectorySeparatorChar));

    private string Work(InstallTransactionPlan plan, string relative) => At(plan.TransactionRoot, relative);

    private string Blob(InstallTransactionPlan plan, string directory, int index) => Work(plan, $"{directory}/{index:D8}");

    private string Active(InstallTransactionPlan plan) => At(plan.InstallRoot, ACTIVE_NAME);

    private long? AvailableSpace(string path) => _fileSystem.DriveInfo.New(_fileSystem.Path.GetPathRoot(path)!).AvailableFreeSpace;

    private void Mutate(string name, Action action)
    {
        _operationBoundary?.Invoke(name, false);
        action();
        _operationBoundary?.Invoke(name, true);
    }
}
