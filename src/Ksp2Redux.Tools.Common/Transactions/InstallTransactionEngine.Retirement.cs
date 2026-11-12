using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Common.Transactions;

public partial class InstallTransactionEngine
{
    private const string RETIREMENT_INTENT = "payload-retirement.json";
    private const string RETIREMENT_CANDIDATE = "payload-retirement.pending";
    private const string RETIREMENT_COMPLETE = "payload-retirement.complete";
    private static readonly string[] PAYLOAD_DIRECTORIES = ["target", "undo", "removed", "rollback"];
    private static readonly JsonSerializerOptions RETIREMENT_JSON = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // The persisted inventory grants deletion only to bytes verified before the first deletion.
    private record PayloadRetirement(int FormatVersion, string PlanDigest, string AuthorizationSha256,
        InstallTransactionResult Result, byte[] Source, byte[] Work);

    /// <summary>
    /// Retire exact disposable payloads of a terminal Windows transaction while retaining its authority and journals.
    /// </summary>
    /// <remarks>
    /// The coordinator must hold its lease, verify a published head with no pending transaction and verify
    /// the complete archive against the stock contract. The installation lease and archive handle remain held
    /// during retirement. Interrupted deletion retries only the saved inventory. Completed retirement cannot
    /// reacquire recreated payloads. Source and work roots must be separately owned disposable stages.
    /// </remarks>
    /// <param name="plan">The independently pinned terminal transaction plan.</param>
    /// <param name="result">The independently published terminal result.</param>
    /// <param name="source">The exact source-stage contract, including empty directories.</param>
    /// <param name="authorizationSha256">The digest binding the coordinator's durable head and operation authority.</param>
    /// <param name="ct">The cancellation token, leaving a retryable intent on interruption.</param>
    /// <param name="engineStorageWasAbsent">True only when durable independent authority witnessed absent engine storage after terminal rollback.</param>
    /// <exception cref="InvalidDataException">Thrown when paths, terminal evidence or inventory bytes conflict.</exception>
    /// <exception cref="IOException">Thrown when locking, archive verification or retirement fails.</exception>
    public void RetireTerminalPayloads(InstallTransactionPlan plan, InstallTransactionResult result,
        StockBaselineManifest source, string authorizationSha256, CancellationToken ct = default,
        bool engineStorageWasAbsent = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Payload retirement is verified only on Windows.");
        if (!Enum.IsDefined(result) || authorizationSha256.Length != 64 ||
            !authorizationSha256.All(Uri.IsHexDigit) || authorizationSha256 != authorizationSha256.ToUpperInvariant())
            throw new InvalidDataException("Retirement requires exact published authority.");
        ValidateRoots(plan, false);
        using FileSystemStream lease = AcquireLock(plan);
        RequireAbsent(Active(plan));
        RequireWindowsPublicationCandidatesAbsent(plan, true);
        RequireWindowsSecurityCandidatesAbsent(plan);
        if (engineStorageWasAbsent)
        {
            if (result != InstallTransactionResult.RolledBack)
                throw new InvalidDataException("Unstarted engine storage cannot authorize committed retirement.");
            if (!Exists(Work(plan, RETIREMENT_COMPLETE)))
                VerifyGeneration(plan, false, ct);
            PrepareRetirementWithoutEngineStorage(plan);
        }
        RequireBytes(Work(plan, "plan.json"), plan.ToJson(), 48 * 1024 * 1024);
        if (!engineStorageWasAbsent)
            RequireTerminalRetirementJournal(plan, result);
        string intentPath = Work(plan, RETIREMENT_INTENT);
        Safe(intentPath);
        PayloadRetirement intent;
        if (Exists(intentPath))
        {
            byte[] bytes = ReadBounded(intentPath, 48 * 1024 * 1024);
            try
            {
                intent = JsonSerializer.Deserialize<PayloadRetirement>(bytes, RETIREMENT_JSON)
                    ?? throw new InvalidDataException("Retirement intent is empty.");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException("Retirement intent is unreadable. Evidence retained.", failure);
            }
            if (!bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(intent, RETIREMENT_JSON)))
                throw new InvalidDataException("Retirement intent is noncanonical.");
        }
        else
        {
            RequireAbsent(Work(plan, RETIREMENT_COMPLETE));
            if (!engineStorageWasAbsent)
            {
                if (result == InstallTransactionResult.Committed)
                    VerifyWindowsReplacementSecurity(plan);
                else if (ReadState(plan) != "preparing")
                    VerifyWindowsRollbackSecurity(plan);
            }
            VerifyRetirementTree(plan.SourceRoot, source, false, ct, mayBeMissing: plan.MovesSourceFile);
            StockBaselineManifest work = ObserveTerminalWork(plan, result, ct);
            intent = new PayloadRetirement(1, plan.GetDigest(), authorizationSha256, result, source.ToJson(), work.ToJson());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(intent, RETIREMENT_JSON);
            if (bytes.Length > 48 * 1024 * 1024)
                throw new InvalidDataException("Retirement inventory exceeds its metadata budget.");
            PublishRetirementIntent(plan, bytes);
        }
        if (intent.FormatVersion != 1 || intent.PlanDigest != plan.GetDigest() ||
            intent.AuthorizationSha256 != authorizationSha256 || intent.Result != result || intent.Source is null ||
            intent.Work is null || !intent.Source.SequenceEqual(source.ToJson()))
            throw new InvalidDataException("Retirement intent differs from published authority.");
        RequireAbsent(Work(plan, RETIREMENT_CANDIDATE));
        StockBaselineManifest retainedWork = StockBaselineManifest.FromJson(intent.Work);
        if (!retainedWork.ToJson().SequenceEqual(intent.Work))
            throw new InvalidDataException("Retirement work inventory is noncanonical.");
        ValidateRetirementWorkContract(plan, result, retainedWork, engineStorageWasAbsent);
        VerifyRetirementTree(plan.SourceRoot, source, true, ct);
        VerifyRetirementWork(plan, retainedWork, ct);
        byte[] completion = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(intent, RETIREMENT_JSON))));
        string completePath = Work(plan, RETIREMENT_COMPLETE);
        // A complete receipt grants no new deletion, so avoid hashing a stock-sized archive again.
        // Every path that can still delete payloads below holds the exact archive read-only.
        bool completionExists = Exists(completePath);
        using FileSystemStream? archive = completionExists ? null : OpenVerifiedArchive(plan, ct);
        if (completionExists)
        {
            Safe(completePath);
            byte[] actual = ReadBounded(completePath, completion.Length);
            RequirePrefix(actual, completion);
            if (actual.SequenceEqual(completion))
            {
                RequireRetiredPayloadsAbsent(plan);
                return;
            }
            // A partial completion record can only follow complete payload deletion.
            RequireRetiredPayloadsAbsent(plan);
        }
        else
        {
            DeleteRetirementTree(plan.SourceRoot, source, ct);
            foreach (string directory in PAYLOAD_DIRECTORIES)
            {
                string root = Work(plan, directory);
                foreach (StockBaselineFile file in retainedWork.Files.Where(file => file.Path.StartsWith(directory + "/", StringComparison.Ordinal)))
                {
                    DeleteRetirementFile(Work(plan, file.Path), file, ct);
                }
                DeleteRetirementDirectory(root, ct);
            }
            RequireRetiredPayloadsAbsent(plan);
        }
        Safe(completePath);
        byte[] previous = Exists(completePath) ? ReadBounded(completePath, completion.Length) : [];
        RequirePrefix(previous, completion);
        AppendExpected(completePath, previous, completion[previous.Length..], "retirement-complete", completion.Length);
    }

    private void RequireTerminalRetirementJournal(InstallTransactionPlan plan, InstallTransactionResult result)
    {
        var journal = ReadJournal(plan, "journal", "preparing");
        var recovery = ReadJournal(plan, "recovery", journal.State == "preparing" ? "aborting" : "rollingback");
        if ((journal.State is "committed" or "rolledback") && Exists(Work(plan, "recovery")))
            throw new InvalidDataException("A terminal installation journal conflicts with recovery metadata.");
        if (recovery.Bytes.Length != recovery.CompleteLength)
            throw new InvalidDataException("Retirement requires complete terminal journals.");
        bool committed = journal.State == "committed" && recovery.Bytes.Length == 0 &&
            journal.Bytes.Length == journal.CompleteLength;
        bool rolledBack = (journal.State == "rolledback" && recovery.Bytes.Length == 0) ||
            (journal.State is "prepared" or "mutating" or "rollingback" && recovery.State == "rolledback") ||
            (journal.State == "preparing" && recovery.State == "aborted");
        if (result == InstallTransactionResult.Committed ? !committed : !rolledBack)
            throw new InvalidDataException("Retirement outcome conflicts with the terminal journals.");
    }

    private StockBaselineManifest ObserveTerminalWork(InstallTransactionPlan plan, InstallTransactionResult result, CancellationToken ct)
    {
        Dictionary<string, StockBaselineFile> allowed = RetirementWorkFiles(plan, result);
        var files = new List<StockBaselineFile>();
        var directories = new List<string>();
        foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(plan.TransactionRoot))
        {
            Safe(entry);
            string name = _fileSystem.Path.GetFileName(entry);
            if (name == RETIREMENT_CANDIDATE)
            {
                if (!_fileSystem.File.Exists(entry))
                    throw new InvalidDataException("Retirement candidate is not a regular file.");
                continue;
            }
            if (PAYLOAD_DIRECTORIES.Contains(name, StringComparer.Ordinal) && _fileSystem.Directory.Exists(entry))
            {
                directories.Add(name);
                foreach (string blob in _fileSystem.Directory.EnumerateFileSystemEntries(entry))
                {
                    Safe(blob);
                    string relative = name + "/" + _fileSystem.Path.GetFileName(blob);
                    if (!allowed.TryGetValue(relative, out StockBaselineFile? expected) || !_fileSystem.File.Exists(blob))
                        throw new InvalidDataException("Unrecognized terminal payload retained.");
                    ct.ThrowIfCancellationRequested();
                    RequireMatch(blob, expected);
                    files.Add(expected);
                }
                continue;
            }
            bool metadata = name is "plan.json" or "journal" or "recovery" ||
                (IsNativeWindows && name is WINDOWS_PUBLICATION_AUTHORITY or WINDOWS_SIBLING_AUTHORITY);
            if (!metadata || !_fileSystem.File.Exists(entry))
                throw new InvalidDataException("Unrecognized transaction entry retained.");
            if (name is WINDOWS_PUBLICATION_AUTHORITY or WINDOWS_SIBLING_AUTHORITY)
                _ = HasWindowsPublicationAuthority(plan, true);
            byte[] bytes = ReadBounded(entry, name == "plan.json" ? 48 * 1024 * 1024 : JOURNAL_MAX_BYTES);
            files.Add(new StockBaselineFile(name, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return new StockBaselineManifest("terminal-work-v1", "verified terminal payload and retained metadata", files, directories);
    }

    private Dictionary<string, StockBaselineFile> RetirementWorkFiles(InstallTransactionPlan plan, InstallTransactionResult result)
    {
        var files = new Dictionary<string, StockBaselineFile>(StringComparer.Ordinal);
        bool windowsPublication = HasWindowsPublicationAuthority(plan, true);
        for (int index = 0; index < plan.Target.Files.Count; index++)
        {
            StockBaselineFile file = plan.Target.Files[index];
            if (plan.RequiresTargetBlob(file) && ((windowsPublication && plan.CurrentFile(file.Path) is null) || result != InstallTransactionResult.Committed ||
                plan.CurrentFile(file.Path) == file || plan.RetainedUnownedFiles.Contains(file.Path)))
                files.Add($"target/{index:D8}", file with { Path = $"target/{index:D8}" });
        }
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            StockBaselineFile file = plan.Current.Files[index];
            if (plan.IsMovedUndo(file) && result == InstallTransactionResult.Committed)
            {
                string relative = $"removed/{index:D8}";
                files.Add(relative, file with { Path = relative });
            }
            if (!plan.RequiresUndoBlob(file))
                continue;
            foreach (string directory in new[] { "undo", "removed", "rollback" })
            {
                if ((directory == "removed" && plan.TargetFile(file.Path) is not null) ||
                    (directory == "rollback" && result == InstallTransactionResult.Committed))
                    continue;
                string relative = $"{directory}/{index:D8}";
                files.Add(relative, file with { Path = relative });
            }
        }
        return files;
    }

    private void ValidateRetirementWorkContract(InstallTransactionPlan plan, InstallTransactionResult result,
        StockBaselineManifest work, bool engineStorageWasAbsent)
    {
        Dictionary<string, StockBaselineFile> allowed = RetirementWorkFiles(plan, result);
        if (work.Directories.Any(path => !PAYLOAD_DIRECTORIES.Contains(path, StringComparer.Ordinal)) ||
            !work.Files.Any(file => file.Path == "plan.json") ||
            (!engineStorageWasAbsent && !work.Files.Any(file => file.Path is "journal" or "recovery")))
            throw new InvalidDataException("Retirement inventory escaped its fixed owned names.");
        foreach (StockBaselineFile file in work.Files)
        {
            if (file.Path is "plan.json" or "journal" or "recovery" ||
                (IsNativeWindows && file.Path is WINDOWS_PUBLICATION_AUTHORITY or WINDOWS_SIBLING_AUTHORITY))
                continue;
            if (!allowed.TryGetValue(file.Path, out StockBaselineFile? expected) || file != expected)
                throw new InvalidDataException("Retirement inventory escaped its exact payload contracts.");
        }
    }

    private void PrepareRetirementWithoutEngineStorage(InstallTransactionPlan plan)
    {
        Safe(plan.TransactionRoot);
        if (!Exists(plan.TransactionRoot))
            Mutate("retirement-unstarted:create", () => CreateDirectory(plan.TransactionRoot));
        RequireDirectory(plan.TransactionRoot);
        foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(plan.TransactionRoot))
        {
            Safe(entry);
            if (_fileSystem.Path.GetFileName(entry) is not ("plan.json" or RETIREMENT_INTENT or RETIREMENT_CANDIDATE or RETIREMENT_COMPLETE) ||
                !_fileSystem.File.Exists(entry))
                throw new InvalidDataException("Unstarted retirement contains unexplained engine work.");
        }
        string path = Work(plan, "plan.json");
        Safe(path);
        byte[] expected = plan.ToJson();
        byte[] previous = Exists(path) ? ReadBounded(path, expected.Length) : [];
        RequirePrefix(previous, expected);
        if (!previous.SequenceEqual(expected))
            AppendExpected(path, previous, expected[previous.Length..], "retirement-unstarted:plan", expected.Length);
    }

    private void VerifyRetirementWork(InstallTransactionPlan plan, StockBaselineManifest work, CancellationToken ct)
    {
        var metadata = new HashSet<string>([RETIREMENT_INTENT, RETIREMENT_CANDIDATE, RETIREMENT_COMPLETE], StringComparer.Ordinal);
        VerifyRetirementTree(plan.TransactionRoot, work, true, ct, metadata);
        foreach (StockBaselineFile file in work.Files.Where(file => !file.Path.Contains('/')))
        {
            RequireMatch(Work(plan, file.Path), file);
        }
    }

    private void VerifyRetirementTree(string root, StockBaselineManifest manifest, bool allowMissing,
        CancellationToken ct, HashSet<string>? ignoredRootFiles = null, Func<StockBaselineFile, bool>? mayBeMissing = null)
    {
        Safe(root);
        if (!Exists(root))
        {
            if (!allowMissing)
                throw new InvalidDataException("Expected disposable stage is absent before retirement intent.");
            return;
        }
        RequireDirectory(root);
        var files = manifest.Files.ToDictionary(file => At(root, file.Path), StringComparer.Ordinal);
        var directories = manifest.Directories.Select(path => At(root, path)).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                Safe(entry);
                if (directory == root && ignoredRootFiles?.Contains(_fileSystem.Path.GetFileName(entry)) == true)
                {
                    if (!_fileSystem.File.Exists(entry))
                        throw new InvalidDataException("Retirement metadata is not a regular file.");
                    continue;
                }
                if (_fileSystem.Directory.Exists(entry) && directories.Remove(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                if (!_fileSystem.File.Exists(entry) || !files.Remove(entry, out StockBaselineFile? file))
                    throw new InvalidDataException("Unrecognized disposable-stage entry retained.");
                RequireMatch(entry, file);
            }
        }
        // Entries left in the inventories were never found during the verified walk.
        if (!allowMissing)
        {
            string? missing = files.FirstOrDefault(entry => mayBeMissing?.Invoke(entry.Value) != true).Key ??
                directories.FirstOrDefault();
            if (missing is not null)
                throw new InvalidDataException($"Expected disposable-stage entry is missing at '{missing}'.");
        }
    }

    private void PublishRetirementIntent(InstallTransactionPlan plan, byte[] bytes)
    {
        string candidate = Work(plan, RETIREMENT_CANDIDATE);
        Safe(candidate);
        byte[] previous = Exists(candidate) ? ReadBounded(candidate, bytes.Length) : [];
        RequirePrefix(previous, bytes);
        AppendExpected(candidate, previous, bytes[previous.Length..], "retirement-intent", bytes.Length);
        Mutate("retirement-intent:publish", () =>
        {
            RequireBytes(candidate, bytes, bytes.Length);
            RequireAbsent(Work(plan, RETIREMENT_INTENT));
            StockArchive.PublishFile(_fileSystem, candidate, Work(plan, RETIREMENT_INTENT), false);
        });
    }

    private void DeleteRetirementTree(string root, StockBaselineManifest manifest, CancellationToken ct)
    {
        foreach (StockBaselineFile file in manifest.Files)
        {
            DeleteRetirementFile(At(root, file.Path), file, ct);
        }
        foreach (string directory in manifest.Directories.OrderByDescending(Depth))
        {
            DeleteRetirementDirectory(At(root, directory), ct);
        }
        DeleteRetirementDirectory(root, ct);
    }

    private void DeleteRetirementFile(string path, StockBaselineFile expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Safe(path);
        if (!Exists(path))
            return;
        Mutate("retirement-delete-file", () =>
        {
            RequireMatch(path, expected);
            _fileSystem.File.Delete(path);
        });
    }

    private void DeleteRetirementDirectory(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Safe(path);
        if (Exists(path))
            Mutate("retirement-delete-directory", () =>
            {
                RequireDirectory(path);
                _fileSystem.Directory.Delete(path, false);
            });
    }

    private void RequireRetiredPayloadsAbsent(InstallTransactionPlan plan)
    {
        RequireAbsent(plan.SourceRoot);
        RequireAbsent(Work(plan, RETIREMENT_CANDIDATE));
        foreach (string directory in PAYLOAD_DIRECTORIES)
        {
            RequireAbsent(Work(plan, directory));
        }
    }
}
