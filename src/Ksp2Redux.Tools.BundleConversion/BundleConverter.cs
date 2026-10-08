using System.IO.Abstractions;
using Ksp2Redux.Tools.BundleConversion.Bundles;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Retargets stock bundle materials from embedded BRP shader copies to the Redux replacement shader
/// bundles, and verifies or reverts that conversion from its journal.
/// </summary>
/// <remarks>
/// Bundles whose edits all fall in uncompressed blocks, and whose serialized files already list the
/// replacement CAB as an external, are edited in place with same-size 12-byte PPtr writes. Every
/// other bundle gets a converted LZ4 copy under Redux/BundleConversion and its stock file is left
/// alone. Every run is safe to repeat: plans are made from the stock bytes, which the journal lets
/// the converter see through its own earlier edits, and an edit is only ever written over the exact
/// bytes it expects.
/// </remarks>
public sealed class BundleConverter
{
    private const string BUNDLE_PATTERN = "*.bundle";
    private const int PROGRESS_INTERVAL = 100;

    private readonly IFileSystem _fileSystem;
    private readonly BundleConversionOptions _options;
    private readonly JournalStore _journal;

    /// <summary>
    /// Initializes a converter for one KSP2 install.
    /// </summary>
    /// <param name="fileSystem">The file system to read and write through.</param>
    /// <param name="gameDirectory">The KSP2 folder, the one holding KSP2_x64.exe.</param>
    /// <param name="options">Parallelism and progress settings, or null for the defaults.</param>
    public BundleConverter(IFileSystem fileSystem, string gameDirectory, BundleConversionOptions? options = null)
    {
        _fileSystem = fileSystem;
        _options = options ?? new BundleConversionOptions();
        Layout = new BundleConversionLayout(fileSystem, gameDirectory);
        _journal = new JournalStore(fileSystem, Layout.JournalPath);
    }

    /// <summary>
    /// Gets where the converter reads and writes.
    /// </summary>
    public BundleConversionLayout Layout { get; }

    /// <summary>
    /// Scans the bundle folder and plans the conversion without writing anything.
    /// </summary>
    /// <param name="manifest">The replacement shader manifest.</param>
    /// <param name="cancellationToken">Stops the scan between bundles.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="DirectoryNotFoundException">The install has no bundle folder.</exception>
    /// <exception cref="InvalidDataException">An existing journal cannot be read.</exception>
    public ConversionPlan Plan(ShaderManifestIndex manifest, CancellationToken cancellationToken = default) =>
        PlanFrom(manifest, _journal.Read(), cancellationToken);

    /// <summary>
    /// Plans and carries out the conversion, merging with whatever an earlier run already did.
    /// </summary>
    /// <param name="manifest">The replacement shader manifest.</param>
    /// <param name="dryRun">True to plan only and write nothing.</param>
    /// <param name="cancellationToken">Stops the run between bundles. The journal always describes what is on disk.</param>
    /// <returns>What the run found and did.</returns>
    /// <exception cref="DirectoryNotFoundException">The install has no bundle folder.</exception>
    /// <exception cref="InvalidDataException">An existing journal cannot be read.</exception>
    public ConversionResult Apply(ShaderManifestIndex manifest, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var previous = _journal.Read();
        var plan = PlanFrom(manifest, previous, cancellationToken);
        ConversionResult result = new(plan, dryRun, Layout.JournalPath);
        if (dryRun)
        {
            return result;
        }

        var planned = plan.Bundles.ToDictionary(bundle => bundle.File, StringComparer.OrdinalIgnoreCase);
        var previousInPlace = MergeByFile(previous?.InPlace ?? []);
        var previousCopies = (previous?.Rewritten ?? [])
            .GroupBy(entry => entry.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        // A bundle that could not be read this time has no plan, which is not the same as needing
        // nothing. Whatever an earlier run did to it is carried over untouched.
        var unreadable = new HashSet<string>(plan.Errors.Select(error => error.File), StringComparer.OrdinalIgnoreCase);
        var carriedInPlace = previousInPlace.Values.Where(entry => unreadable.Contains(entry.File)).ToList();
        var carriedCopies = previousCopies.Values.Where(entry => unreadable.Contains(entry.File)).ToList();
        foreach (var entry in carriedInPlace)
        {
            previousInPlace.Remove(entry.File);
        }

        foreach (var entry in carriedCopies)
        {
            previousCopies.Remove(entry.File);
        }

        // Undo whatever earlier runs did that this plan no longer wants, before anything new is written.
        var leftovers = RetireInPlaceEdits(previousInPlace, planned, result);
        RetireCopies(previousCopies, planned, result);

        List<JournalInPlaceFile> inPlace = [];
        foreach (var bundle in plan.Bundles.Where(bundle => bundle.Mode == BundleEditMode.InPlace))
        {
            leftovers.TryGetValue(bundle.File, out var foreign);
            leftovers.Remove(bundle.File);
            inPlace.Add(new JournalInPlaceFile
            {
                File = bundle.File,
                Size = bundle.Size,
                Edits =
                [
                    .. bundle.Edits.Select(edit => new JournalEdit { Offset = edit.FileOffset, Old = Hex.Encode(edit.Old), New = Hex.Encode(edit.New) }),
                    .. foreign ?? [],
                ],
            });
        }

        // Edits nobody could put back stay journaled, so a later revert can still try.
        inPlace.AddRange(leftovers.Select(pair => new JournalInPlaceFile { File = pair.Key, Size = previousInPlace[pair.Key].Size, Edits = pair.Value }));
        inPlace.AddRange(carriedInPlace);

        var copies = PlanCopies(plan, previous, previousCopies, manifest, result, cancellationToken);
        ConversionJournal journal = new()
        {
            ShaderManifestSha256 = manifest.ManifestSha256,
            InPlace = [.. inPlace.OrderBy(entry => entry.File, StringComparer.Ordinal)],
            Rewritten = [.. copies.Select(copy => copy.Entry).Concat(carriedCopies).OrderBy(entry => entry.File, StringComparer.Ordinal)],
            Skipped = SkippedEntries(plan),
        };

        // The journal goes to disk before any byte it describes, so an interrupted run can always be
        // reverted. A copy is journaled with an empty hash until it is complete.
        _journal.Write(journal);

        WriteCopies(copies.Where(copy => copy.Pending).ToList(), journal, result, cancellationToken);
        ApplyInPlace(plan, previousInPlace, result, cancellationToken);

        _journal.Write(journal);
        return result;
    }

    /// <summary>
    /// Puts every journaled edit back to stock, deletes the converted copies, then deletes the journal.
    /// </summary>
    /// <param name="cancellationToken">Stops the revert between bundles. The journal is kept until everything is undone.</param>
    /// <returns>What the revert found and did.</returns>
    /// <exception cref="InvalidDataException">The journal cannot be read.</exception>
    public RevertResult Revert(CancellationToken cancellationToken = default)
    {
        var journal = _journal.Read();
        RevertResult result = new(journal is not null);
        if (journal is null)
        {
            return result;
        }

        foreach (var entry in MergeByFile(journal.InPlace).Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevertFile(entry, result);
        }

        foreach (var entry in journal.Rewritten)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveCopy(entry, out var output))
            {
                result.Failures.Add(new BundleProblem(entry.File, $"journal output '{entry.Output}' is not a copy in the conversion folder, left alone"));
                continue;
            }

            try
            {
                if (_fileSystem.File.Exists(output))
                {
                    _fileSystem.File.Delete(output);
                    result.CopiesDeleted++;
                }

                _fileSystem.File.Delete(output + ".tmp");
            }
            catch (IOException e)
            {
                result.Failures.Add(new BundleProblem(entry.File, $"could not delete {output}: {e.Message}"));
            }
        }

        if (result.Failures.Count == 0)
        {
            _journal.Delete();
            result.JournalDeleted = true;
            DeleteIfEmpty(Layout.OutputDirectory);
            DeleteIfEmpty(Layout.ConversionDirectory);
        }

        return result;
    }

    /// <summary>
    /// Checks every journaled edit and converted copy against the files on disk.
    /// </summary>
    /// <param name="cancellationToken">Stops the check between bundles.</param>
    /// <returns>The state of each edit and copy.</returns>
    /// <exception cref="InvalidDataException">The journal cannot be read.</exception>
    public VerifyResult Verify(CancellationToken cancellationToken = default)
    {
        var journal = _journal.Read();
        VerifyResult result = new(journal is not null, journal?.ShaderManifestSha256);
        if (journal is null)
        {
            return result;
        }

        foreach (var entry in MergeByFile(journal.InPlace).Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenBundle(entry.File, entry.Size, FileAccess.Read, out var stream))
            {
                result.Edits.AddRange(entry.Edits.Select(edit => new EditStatus(entry.File, edit.Offset, EditState.FileChanged)));
                continue;
            }

            using (stream)
            {
                foreach (var edit in entry.Edits)
                {
                    result.Edits.Add(new EditStatus(entry.File, edit.Offset, Classify(stream, edit)));
                }
            }
        }

        var outputs = new OutputStatus[journal.Rewritten.Count];
        Parallel.For(0, outputs.Length, ParallelOptions(cancellationToken), index =>
        {
            var entry = journal.Rewritten[index];
            outputs[index] = new OutputStatus(entry.File, entry.Output, CheckCopy(entry));
        });

        result.Outputs.AddRange(outputs);
        return result;
    }

    private ConversionPlan PlanFrom(ShaderManifestIndex manifest, ConversionJournal? journal, CancellationToken cancellationToken)
    {
        if (!_fileSystem.Directory.Exists(Layout.BundleDirectory))
        {
            throw new DirectoryNotFoundException($"No bundle folder at {Layout.BundleDirectory}.");
        }

        var paths = _fileSystem.Directory
            .EnumerateFiles(Layout.BundleDirectory, BUNDLE_PATTERN, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var journaled = MergeByFile(journal?.InPlace ?? []);
        var scanned = new ScannedBundle[paths.Count];
        var done = 0;

        Parallel.For(0, paths.Count, ParallelOptions(cancellationToken), index =>
        {
            var path = paths[index];
            journaled.TryGetValue(_fileSystem.Path.GetFileName(path), out var entry);
            scanned[index] = BundleScanner.Scan(_fileSystem, path, StockOverlay(path, entry));

            var count = Interlocked.Increment(ref done);
            if (count % PROGRESS_INTERVAL == 0 || count == paths.Count)
            {
                _options.Progress?.Report($"Scanned {count} of {paths.Count} bundles");
            }
        });

        return ConversionPlanner.Plan(scanned, manifest);
    }

    // Shows each journaled edit's stock bytes wherever the file holds its converted bytes, so a
    // rerun plans from stock even though an earlier run edited the file.
    private IReadOnlyList<KeyValuePair<long, byte[]>> StockOverlay(string path, JournalInPlaceFile? entry)
    {
        if (entry is null || !TryOpenBundle(entry.File, entry.Size, FileAccess.Read, out var stream))
        {
            return [];
        }

        using (stream)
        {
            List<KeyValuePair<long, byte[]>> overlay = [];
            foreach (var edit in entry.Edits)
            {
                if (TryDecode(edit, out var oldBytes, out var newBytes) &&
                    InPlaceEditor.Classify(stream, edit.Offset, oldBytes, newBytes) == EditState.Applied)
                {
                    overlay.Add(new KeyValuePair<long, byte[]>(edit.Offset, oldBytes));
                }
            }

            return overlay;
        }
    }

    // Journal edits for bundles the new plan does not edit in place, or at offsets it does not
    // touch, are put back to stock. Returns the edits that could not be, per bundle.
    private Dictionary<string, List<JournalEdit>> RetireInPlaceEdits(
        Dictionary<string, JournalInPlaceFile> previous,
        Dictionary<string, BundleEditPlan> planned,
        ConversionResult result)
    {
        Dictionary<string, List<JournalEdit>> leftovers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previous.Values)
        {
            HashSet<long> kept = [];
            if (planned.TryGetValue(entry.File, out var plan) && plan.Mode == BundleEditMode.InPlace)
            {
                kept.UnionWith(plan.Edits.Select(edit => edit.FileOffset));
            }

            var retiring = entry.Edits.Where(edit => !kept.Contains(edit.Offset)).ToList();
            if (retiring.Count == 0)
            {
                continue;
            }

            if (!TryOpenBundle(entry.File, entry.Size, FileAccess.ReadWrite, out var stream))
            {
                result.DroppedEntries.Add(new BundleProblem(entry.File, "the bundle is missing or changed size, its journal entry was dropped"));
                continue;
            }

            using (stream)
            {
                foreach (var edit in retiring)
                {
                    if (!TryDecode(edit, out var oldBytes, out var newBytes))
                    {
                        result.DroppedEntries.Add(new BundleProblem(entry.File, $"unreadable journal edit at {edit.Offset} was dropped"));
                        continue;
                    }

                    switch (InPlaceEditor.Classify(stream, edit.Offset, oldBytes, newBytes))
                    {
                        case EditState.Applied:
                            InPlaceEditor.WriteIfMatches(stream, edit.Offset, newBytes, oldBytes);
                            result.EditsReverted++;
                            break;
                        case EditState.Foreign:
                            result.ForeignEdits.Add(new EditStatus(entry.File, edit.Offset, EditState.Foreign));
                            AddLeftover(leftovers, entry.File, edit);
                            break;
                    }
                }
            }
        }

        return leftovers;
    }

    private static void AddLeftover(Dictionary<string, List<JournalEdit>> leftovers, string file, JournalEdit edit)
    {
        if (!leftovers.TryGetValue(file, out var list))
        {
            list = [];
            leftovers.Add(file, list);
        }

        list.Add(edit);
    }

    private void RetireCopies(Dictionary<string, JournalRewrittenFile> previous, Dictionary<string, BundleEditPlan> planned, ConversionResult result)
    {
        foreach (var entry in previous.Values)
        {
            if (planned.TryGetValue(entry.File, out var plan) && plan.Mode == BundleEditMode.Rewrite)
            {
                continue;
            }

            if (TryResolveCopy(entry, out var output) && _fileSystem.File.Exists(output))
            {
                _fileSystem.File.Delete(output);
                result.CopiesDeleted++;
            }
        }
    }

    private List<PlannedCopy> PlanCopies(
        ConversionPlan plan,
        ConversionJournal? previous,
        Dictionary<string, JournalRewrittenFile> previousCopies,
        ShaderManifestIndex manifest,
        ConversionResult result,
        CancellationToken cancellationToken)
    {
        var bundles = plan.Bundles.Where(bundle => bundle.Mode == BundleEditMode.Rewrite).ToList();
        var copies = new PlannedCopy[bundles.Count];
        var sameManifest = previous is not null && previous.ShaderManifestSha256 == manifest.ManifestSha256;

        Parallel.For(0, bundles.Count, ParallelOptions(cancellationToken), index =>
        {
            var bundle = bundles[index];
            var stockSha256 = HashFile(Layout.BundlePath(bundle.File));
            var relative = BundleConversionLayout.RelativeOutput(bundle.File);

            // A copy is reused only when nothing it was made from has changed: the same manifest, the
            // same stock bytes, and the copy itself still intact.
            if (sameManifest &&
                previousCopies.TryGetValue(bundle.File, out var existing) &&
                existing.StockSha256 == stockSha256 &&
                existing.OutputSha256.Length > 0 &&
                existing.Output == relative &&
                CheckOutputHash(Layout.Resolve(relative), existing.OutputSha256) == OutputState.Ok)
            {
                copies[index] = new PlannedCopy(bundle, existing, false);
                return;
            }

            copies[index] = new PlannedCopy(
                bundle,
                new JournalRewrittenFile
                {
                    File = bundle.File,
                    StockSize = bundle.Size,
                    StockSha256 = stockSha256,
                    Output = relative,
                    OutputSha256 = "",
                },
                true);
        });

        result.CopiesUpToDate = copies.Count(copy => !copy.Pending);
        return [.. copies];
    }

    private void WriteCopies(List<PlannedCopy> pending, ConversionJournal journal, ConversionResult result, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        _fileSystem.Directory.CreateDirectory(Layout.OutputDirectory);
        var done = 0;
        var gate = new object();

        Parallel.For(0, pending.Count, ParallelOptions(cancellationToken), index =>
        {
            var copy = pending[index];
            var output = Layout.Resolve(copy.Entry.Output);
            var temporary = output + ".tmp";
            try
            {
                using (var stock = _fileSystem.FileStream.New(Layout.BundlePath(copy.Bundle.File), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = _fileSystem.FileStream.New(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    BundleRewriter.Write(stock, copy.Bundle, target);
                }

                copy.Entry.OutputSha256 = HashFile(temporary);
                AtomicFile.Replace(_fileSystem, temporary, output);
                lock (gate)
                {
                    result.CopiesWritten++;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A copy from an earlier run is stale now and loses its journal entry, so it goes too
                // rather than sitting on disk where nothing would ever clean it up.
                TryDelete(temporary);
                TryDelete(output);
                lock (gate)
                {
                    result.Failures.Add(new BundleProblem(copy.Bundle.File, $"converted copy failed: {e.Message}"));
                    journal.Rewritten.Remove(copy.Entry);
                }
            }

            var count = Interlocked.Increment(ref done);
            if (count % 25 == 0 || count == pending.Count)
            {
                _options.Progress?.Report($"Wrote {count} of {pending.Count} converted copies");
            }
        });
    }

    private void ApplyInPlace(
        ConversionPlan plan,
        Dictionary<string, JournalInPlaceFile> previous,
        ConversionResult result,
        CancellationToken cancellationToken)
    {
        var bundles = plan.Bundles.Where(bundle => bundle.Mode == BundleEditMode.InPlace).ToList();
        var gate = new object();

        Parallel.For(0, bundles.Count, ParallelOptions(cancellationToken), index =>
        {
            var bundle = bundles[index];
            previous.TryGetValue(bundle.File, out var earlier);
            var earlierEdits = earlier?.Edits.ToDictionary(edit => edit.Offset) ?? [];

            int written = 0, already = 0, updated = 0;
            List<EditStatus> foreign = [];
            try
            {
                using var stream = _fileSystem.FileStream.New(Layout.BundlePath(bundle.File), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                foreach (var edit in bundle.Edits)
                {
                    switch (InPlaceEditor.Classify(stream, edit.FileOffset, edit.Old, edit.New))
                    {
                        case EditState.Applied:
                            already++;
                            break;
                        case EditState.Stock:
                            InPlaceEditor.WriteIfMatches(stream, edit.FileOffset, edit.Old, edit.New);
                            written++;
                            break;
                        default:
                            // An earlier run's edit to the same stock bytes, pointing at an older target.
                            if (earlierEdits.TryGetValue(edit.FileOffset, out var previousEdit) &&
                                previousEdit.Old == Hex.Encode(edit.Old) &&
                                TryDecode(previousEdit, out _, out var previousNew) &&
                                InPlaceEditor.WriteIfMatches(stream, edit.FileOffset, previousNew, edit.New))
                            {
                                updated++;
                            }
                            else
                            {
                                foreign.Add(new EditStatus(bundle.File, edit.FileOffset, EditState.Foreign));
                            }

                            break;
                    }
                }

                stream.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lock (gate)
                {
                    result.Failures.Add(new BundleProblem(bundle.File, $"in-place edit failed: {e.Message}"));
                }
            }

            lock (gate)
            {
                result.EditsWritten += written;
                result.EditsAlreadyApplied += already;
                result.EditsUpdated += updated;
                result.ForeignEdits.AddRange(foreign);
            }
        });
    }

    private void RevertFile(JournalInPlaceFile entry, RevertResult result)
    {
        if (!TryOpenBundle(entry.File, entry.Size, FileAccess.ReadWrite, out var stream))
        {
            result.Unreverted.AddRange(entry.Edits.Select(edit => new EditStatus(entry.File, edit.Offset, EditState.FileChanged)));
            return;
        }

        try
        {
            using (stream)
            {
                foreach (var edit in entry.Edits)
                {
                    if (!TryDecode(edit, out var oldBytes, out var newBytes))
                    {
                        result.Unreverted.Add(new EditStatus(entry.File, edit.Offset, EditState.Foreign));
                        continue;
                    }

                    switch (InPlaceEditor.Classify(stream, edit.Offset, oldBytes, newBytes))
                    {
                        case EditState.Applied:
                            InPlaceEditor.WriteIfMatches(stream, edit.Offset, newBytes, oldBytes);
                            result.EditsReverted++;
                            break;
                        case EditState.Stock:
                            result.EditsAlreadyStock++;
                            break;
                        default:
                            result.Unreverted.Add(new EditStatus(entry.File, edit.Offset, EditState.Foreign));
                            break;
                    }
                }

                stream.Flush();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result.Failures.Add(new BundleProblem(entry.File, $"revert failed: {e.Message}"));
        }
    }

    private OutputState CheckCopy(JournalRewrittenFile entry)
    {
        if (!TryResolveCopy(entry, out var output))
        {
            return OutputState.Invalid;
        }

        var stock = Layout.BundlePath(entry.File);
        if (!_fileSystem.File.Exists(stock) ||
            _fileSystem.FileInfo.New(stock).Length != entry.StockSize ||
            HashFile(stock) != entry.StockSha256)
        {
            return OutputState.StockChanged;
        }

        return entry.OutputSha256.Length == 0 ? OutputState.Pending : CheckOutputHash(output, entry.OutputSha256);
    }

    private OutputState CheckOutputHash(string output, string expected)
    {
        if (!_fileSystem.File.Exists(output))
        {
            return OutputState.Missing;
        }

        return HashFile(output) == expected ? OutputState.Ok : OutputState.HashMismatch;
    }

    // The journal is a file anything could have edited, so its paths are checked before a revert
    // deletes or writes what they name: a bundle entry must be a plain file name, and a copy must
    // be exactly where this converter puts the copy of that bundle.
    private bool TryResolveCopy(JournalRewrittenFile entry, out string output)
    {
        output = "";
        if (!IsPlainFileName(entry.File) || entry.Output != BundleConversionLayout.RelativeOutput(entry.File))
        {
            return false;
        }

        output = Layout.Resolve(entry.Output);
        return true;
    }

    private bool TryOpenBundle(string file, long expectedSize, FileAccess access, out Stream stream)
    {
        stream = Stream.Null;
        if (!IsPlainFileName(file))
        {
            return false;
        }

        var path = Layout.BundlePath(file);
        if (!_fileSystem.File.Exists(path))
        {
            return false;
        }

        var opened = _fileSystem.FileStream.New(path, FileMode.Open, access, FileShare.Read);
        if (opened.Length != expectedSize)
        {
            opened.Dispose();
            return false;
        }

        stream = opened;
        return true;
    }

    private static EditState Classify(Stream stream, JournalEdit edit) =>
        TryDecode(edit, out var oldBytes, out var newBytes)
            ? InPlaceEditor.Classify(stream, edit.Offset, oldBytes, newBytes)
            : EditState.Foreign;

    private static bool TryDecode(JournalEdit edit, out byte[] oldBytes, out byte[] newBytes)
    {
        try
        {
            oldBytes = Hex.Decode(edit.Old);
            newBytes = Hex.Decode(edit.New);
            return oldBytes.Length > 0 && oldBytes.Length == newBytes.Length;
        }
        catch (FormatException)
        {
            oldBytes = [];
            newBytes = [];
            return false;
        }
    }

    private static bool IsPlainFileName(string file) =>
        file.Length > 0 && file.IndexOfAny(['/', '\\', ':']) < 0 && file != "." && file != "..";

    private static Dictionary<string, JournalInPlaceFile> MergeByFile(IEnumerable<JournalInPlaceFile> entries)
    {
        Dictionary<string, JournalInPlaceFile> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!merged.TryGetValue(entry.File, out var existing))
            {
                merged.Add(entry.File, new JournalInPlaceFile { File = entry.File, Size = entry.Size, Edits = [.. entry.Edits] });
                continue;
            }

            var offsets = existing.Edits.Select(edit => edit.Offset).ToHashSet();
            existing.Edits.AddRange(entry.Edits.Where(edit => offsets.Add(edit.Offset)));
        }

        return merged;
    }

    private static List<JournalSkipped> SkippedEntries(ConversionPlan plan) =>
    [
        .. plan.Skipped
            .Select(skip => new JournalSkipped { File = skip.File, Shader = skip.Shader, Reason = skip.Reason })
            .GroupBy(skip => (skip.File, skip.Shader, skip.Reason))
            .Select(group => group.First())
            .OrderBy(skip => skip.File, StringComparer.Ordinal)
            .ThenBy(skip => skip.Shader, StringComparer.Ordinal),
    ];

    private string HashFile(string path)
    {
        using var stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Hex.Sha256(stream);
    }

    private void TryDelete(string path)
    {
        try
        {
            _fileSystem.File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless: the next run overwrites it.
        }
    }

    private void DeleteIfEmpty(string directory)
    {
        if (_fileSystem.Directory.Exists(directory) && !_fileSystem.Directory.EnumerateFileSystemEntries(directory).Any())
        {
            _fileSystem.Directory.Delete(directory);
        }
    }

    private ParallelOptions ParallelOptions(CancellationToken cancellationToken) => new()
    {
        MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism),
        CancellationToken = cancellationToken,
    };

    private sealed class PlannedCopy
    {
        public PlannedCopy(BundleEditPlan bundle, JournalRewrittenFile entry, bool pending)
        {
            Bundle = bundle;
            Entry = entry;
            Pending = pending;
        }

        public BundleEditPlan Bundle { get; }

        public JournalRewrittenFile Entry { get; }

        public bool Pending { get; }
    }
}
