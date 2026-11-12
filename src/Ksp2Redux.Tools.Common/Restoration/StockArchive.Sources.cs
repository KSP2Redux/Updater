using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Common.Wrappers;

namespace Ksp2Redux.Tools.Common.Restoration;

/// <summary>
/// Exact source observations retained between preparation and use, without granting stock authority.
/// </summary>
/// <param name="CanonicalArchive">True if the held archive alone supplies the complete stock contract.</param>
/// <param name="Journal">The relative conversion journal pin, or null when no journal was used.</param>
/// <param name="CurrentFiles">The actual current-file pins for sources used outside the ZIP.</param>
/// <param name="JournalRanges">The observed original or converted state of every consumed journal range.</param>
public record StockArchiveSources(bool CanonicalArchive, StockBaselineFile? Journal,
    StockBaselineFile[] CurrentFiles, string[] JournalRanges);

public partial class StockArchive
{
    private readonly InstallTransactionEngine? _recoveryPaths;

    /// <summary>
    /// Verify complete original-byte coverage from an existing ZIP and exact current stock files.
    /// </summary>
    /// <remarks>
    /// A canonical manifest must match identity and provenance exactly. Legacy Windows separators are
    /// normalized, but aliases, links and unsafe paths are rejected. Unknown safe entries stay in the
    /// original ZIP and never join the stock contract. No source or installation bytes are changed.
    /// </remarks>
    /// <param name="archivePath">The independently pinned existing backup.</param>
    /// <param name="currentRoot">The current installation used only for hash-matching original stock bytes.</param>
    /// <param name="expected">The complete independently trusted stock contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True if the archive itself is canonical and complete, false if exact legacy recovery is required.</returns>
    public async Task<bool> VerifyAvailableSourcesAsync(string archivePath, string currentRoot, StockBaselineManifest expected,
        CancellationToken ct = default) => (await InspectAvailableSourcesAsync(archivePath, currentRoot, expected, ct)).CanonicalArchive;

    /// <summary>
    /// Verify complete original coverage and observe exact held ZIP, current-file and journal sources.
    /// </summary>
    /// <param name="archivePath">The independently pinned existing backup.</param>
    /// <param name="currentRoot">The controlled selected installation root.</param>
    /// <param name="expected">The independently trusted complete stock contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="sources">The previous source observations to independently reverify, or null for initial observation.</param>
    /// <returns>The exact source observations and range states used for verified coverage.</returns>
    public Task<StockArchiveSources> InspectAvailableSourcesAsync(string archivePath, string currentRoot,
        StockBaselineManifest expected, CancellationToken ct = default, StockArchiveSources? sources = null) =>
        WithAvailableSourcesAsync(archivePath, currentRoot, expected, null, ct, sources);

    /// <summary>
    /// Stream verified original files to a consumer without materializing a second source tree.
    /// </summary>
    /// <param name="archivePath">The independently pinned existing backup.</param>
    /// <param name="currentRoot">The current tree supplying only exact original files.</param>
    /// <param name="expected">The complete trusted stock contract.</param>
    /// <param name="consume">The consumer, which must verify any persisted output against its file contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="sources">The previous source observations to reverify, or null for fresh independently verified sources.</param>
    /// <param name="requestedPaths">The paths to stream after verifying complete coverage, or null to stream all files.</param>
    /// <returns>The task completing after complete coverage verification and all consumers.</returns>
    public async Task ReadAvailableSourcesAsync(string archivePath, string currentRoot, StockBaselineManifest expected,
        Func<StockBaselineFile, Stream, CancellationToken, Task> consume, CancellationToken ct = default,
        StockArchiveSources? sources = null, IReadOnlySet<string>? requestedPaths = null)
    {
        await WithAvailableSourcesAsync(archivePath, currentRoot, expected, async open =>
        {
            foreach (StockBaselineFile file in expected.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (requestedPaths is not null && !requestedPaths.Contains(file.Path))
                    continue;
                using Stream source = open(file);
                await consume(file, source, ct);
            }
        }, ct, sources);
    }

    /// <summary>
    /// Build and independently verify a complete candidate from exact ZIP or current-file sources.
    /// </summary>
    /// <param name="archivePath">The existing backup, retained unchanged.</param>
    /// <param name="currentRoot">The current tree supplying only exact original files.</param>
    /// <param name="candidatePath">The absent owned candidate destination.</param>
    /// <param name="expected">The complete trusted stock contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="sources">The previous source observations to reverify, or null for fresh independently verified sources.</param>
    /// <returns>The task completing after verified candidate publication.</returns>
    public async Task CreateRecoveredAsync(string archivePath, string currentRoot, string candidatePath,
        StockBaselineManifest expected, CancellationToken ct = default, StockArchiveSources? sources = null)
    {
        await WithAvailableSourcesAsync(archivePath, currentRoot, expected,
            open => CreateFromSourcesAsync(candidatePath, expected, open, ct), ct, sources);
    }

    private async Task<StockArchiveSources> WithAvailableSourcesAsync(string archivePath, string currentRoot,
        StockBaselineManifest expected, Func<Func<StockBaselineFile, Stream>, Task>? consume, CancellationToken ct,
        StockArchiveSources? expectedSources)
    {
        _ = ValidateContract(expected);
        string path = DiskPath(archivePath);
        string current = DiskPath(currentRoot);
        ValidateRecoveryPath(path);
        ValidateRecoveryPath(current);
        EnsureSafePath(path, false);
        EnsureSafePath(current, true);
        await using FileSystemStream input = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, _limits.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > _limits.MaxArchiveBytes)
            throw new InvalidDataException("Existing ZIP exceeds the archive budget.");
        try
        {
            CheckZipDirectoryBudget(input, null, ct);
        }
        catch (EndOfStreamException failure)
        {
            throw new InvalidDataException("Existing ZIP metadata is truncated.", failure);
        }
        input.Position = 0;
        using IZipArchive archive = _zipFileService.NewArchive(input, ZipArchiveMode.Read, true);
        var entries = new Dictionary<string, IZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (IZipArchiveEntry entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/');
            bool directory = normalized.EndsWith('/');
            string name = directory ? normalized[..^1] : normalized;
            if (name != StockBaselineManifest.MANIFEST_PATH && StockBaselineManifest.NormalizePath(name) != name)
                throw new InvalidDataException("Existing ZIP contains a noncanonical path.");
            int type = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (type != 0 && type != (directory ? 0x4000 : 0x8000)) ||
                (!directory && (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0) ||
                entry.Length < 0 || entry.Length > _limits.MaxFileBytes || (directory && entry.Length != 0) ||
                !entries.TryAdd(name, entry))
                throw new InvalidDataException("Existing ZIP contains a link, alias, duplicate or out-of-budget entry.");
            if (name != StockBaselineManifest.MANIFEST_PATH)
                declaredBytes = checked(declaredBytes + entry.Length);
            if (declaredBytes > _limits.MaxTotalBytes)
                throw new InvalidDataException("Existing ZIP declared bytes exceed the recovery budget.");
        }
        bool canonical = entries.ContainsKey(StockBaselineManifest.MANIFEST_PATH);
        if (canonical)
            await VerifyAsync(input, expected, ct);
        var fromArchive = new HashSet<string>(StringComparer.Ordinal);
        var gaps = new List<string>();
        var buffer = new byte[_limits.BufferBytes];
        var held = new Dictionary<string, (FileSystemStream Source, IReadOnlyList<KeyValuePair<long, byte[]>> Patches)>(StringComparer.Ordinal);
        var pins = new List<StockBaselineFile>();
        var ranges = new List<string>();
        FileSystemStream? journalLease = null;
        StockBaselineFile? journalPin = null;
        Dictionary<string, JournalInPlaceFile>? journalFiles = null;
        string? journalFailure = null;
        bool journalRead = false;
        try
        {
            foreach (StockBaselineFile file in expected.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (canonical)
                {
                    // The complete verification above used this same held read-only ZIP stream.
                    fromArchive.Add(file.Path);
                    continue;
                }
                string zipReason = "ZIP entry missing";
                if (entries.TryGetValue(file.Path, out IZipArchiveEntry? entry) &&
                    !entry.FullName.Replace('\\', '/').EndsWith('/') && entry.Length == file.Length)
                {
                    try
                    {
                        using Stream source = entry.Open();
                        await CopyVerifiedAsync(source, null, file, buffer, ct);
                        fromArchive.Add(file.Path);
                        continue;
                    }
                    catch (InvalidDataException)
                    {
                        zipReason = "ZIP content differs from trusted stock";
                    }
                }
                else if (entry is not null)
                {
                    zipReason = entry.FullName.Replace('\\', '/').EndsWith('/')
                        ? "ZIP path is a directory" : "ZIP length differs from trusted stock";
                }
                string active = ContainedPath(current, file.Path);
                ValidateRecoveryPath(active);
                try
                {
                    EnsureSafePath(active, false, true);
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
                if (_fileSystem.File.Exists(active))
                {
                    FileSystemStream source = _fileSystem.FileStream.New(active, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try
                    {
                        await CopyVerifiedAsync(source, null, file, buffer, ct);
                        held.Add(file.Path, (source, []));
                        pins.Add(file);
                        continue;
                    }
                    catch (InvalidDataException)
                    {
                    }
                    catch
                    {
                        source.Dispose();
                        throw;
                    }
                    try
                    {
                        if (!journalRead)
                        {
                            journalRead = true;
                            try
                            {
                                (journalLease, journalPin, journalFiles) = await ReadStockJournalAsync(current, expected, ct);
                            }
                            catch (InvalidDataException failure)
                            {
                                journalFailure = failure.Message;
                            }
                        }
                        if (journalFiles is null || !journalFiles.TryGetValue(file.Path, out JournalInPlaceFile? declaration))
                            throw new InvalidDataException(journalFailure ?? "journal has no original in-place payload for this stock path");
                        if (source.Length != file.Length)
                            throw new InvalidDataException("journal/current/trusted file sizes differ");
                        source.Position = 0;
                        string currentHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(source, ct));
                        var pin = new StockBaselineFile(file.Path, source.Length, currentHash);
                        IReadOnlyList<KeyValuePair<long, byte[]>> patches = await StockJournalPatchesAsync(source, declaration, file, ranges, ct);
                        source.Position = 0;
                        using (Stream overlay = ReadOnlyByteOverlay.Open(source, patches))
                            await CopyVerifiedAsync(overlay, null, file, buffer, ct);
                        held.Add(file.Path, (source, patches));
                        pins.Add(pin);
                        continue;
                    }
                    catch (InvalidDataException failure)
                    {
                        gaps.Add($"{file.Path}: {zipReason}, current file differs from trusted stock, journal recovery: {failure.Message}");
                    }
                    finally
                    {
                        if (!held.ContainsKey(file.Path))
                            source.Dispose();
                    }
                    continue;
                }
                gaps.Add($"{file.Path}: {zipReason}, current file missing, journal recovery requires the original path and retained byte payload");
            }
            if (gaps.Count != 0)
            {
                var failure = new InvalidDataException("Original stock coverage is incomplete. Supply exact original files before conversion:\n" + string.Join('\n', gaps));
                failure.Data["StockArchiveCoverageGaps"] = gaps.ToArray();
                failure.Data["StockArchiveJournalRanges"] = ranges.ToArray();
                throw failure;
            }
            var actualSources = new StockArchiveSources(canonical, journalPin, pins.ToArray(), ranges.ToArray());
            if (expectedSources is not null && (actualSources.CanonicalArchive != expectedSources.CanonicalArchive ||
                actualSources.Journal != expectedSources.Journal ||
                !actualSources.CurrentFiles.SequenceEqual(expectedSources.CurrentFiles) ||
                !actualSources.JournalRanges.SequenceEqual(expectedSources.JournalRanges, StringComparer.Ordinal)))
                throw new InvalidDataException("Original-byte source observations changed after preparation. All input bytes retained.");
            if (consume is not null)
            {
                await consume(file =>
                {
                    if (fromArchive.Contains(file.Path))
                        return entries[file.Path].Open();
                    var source = held[file.Path];
                    source.Source.Position = 0;
                    return ReadOnlyByteOverlay.Open(source.Source, source.Patches);
                });
            }
            return actualSources;
        }
        finally
        {
            foreach (var source in held.Values)
                source.Source.Dispose();
            journalLease?.Dispose();
        }
    }

    private async Task CreateFromSourcesAsync(string archivePath, StockBaselineManifest expected,
        Func<StockBaselineFile, Stream> openSource, CancellationToken ct)
    {
        byte[] manifest = ValidateContract(expected);
        string destination = DiskPath(archivePath);
        string parent = _fileSystem.Path.GetDirectoryName(destination)!;
        ValidateRecoveryPath(destination);
        ValidateRecoveryPath(parent);
        EnsureSafePath(parent, true);
        EnsureAbsent(destination);
        EnsureSpace(parent, EstimateArchiveBytes(expected, manifest));
        string staging = _fileSystem.Path.Combine(parent, $".stock-archive-{Guid.NewGuid():N}.tmp");
        ValidateRecoveryPath(staging);
        bool owned = false;
        try
        {
            await using (FileSystemStream output = _fileSystem.FileStream.New(staging, FileMode.CreateNew,
                             FileAccess.ReadWrite, FileShare.None, _limits.BufferBytes, FileOptions.Asynchronous))
            {
                owned = true;
                using (IZipArchive archive = _zipFileService.NewArchive(ArchiveOutput(output, parent), ZipArchiveMode.Create, true))
                    await WriteContentsAsync(archive, expected, manifest, openSource, null, ct);
                if (output.Length > _limits.MaxArchiveBytes)
                    throw new InvalidDataException("Recovered archive exceeds the byte budget.");
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            await VerifyAsync(staging, expected, ct);
            ct.ThrowIfCancellationRequested();
            EnsureSafePath(staging, false);
            ValidateRecoveryPath(staging);
            ValidateRecoveryPath(destination);
            EnsureAbsent(destination);
            PublishFile(_fileSystem, staging, destination, false);
            owned = false;
        }
        catch (Exception failure)
        {
            if (owned)
            {
                try
                {
                    ValidateRecoveryPath(staging);
                    CleanupFile(staging, failure);
                }
                catch (Exception cleanupFailure)
                {
                    failure.Data["StockArchiveCleanupFailure"] = cleanupFailure;
                    failure.Data["StockArchiveStagingPath"] = staging;
                }
            }
            throw;
        }
    }

    private async Task WriteContentsAsync(IZipArchive archive, StockBaselineManifest expected, byte[] manifest,
        Func<StockBaselineFile, Stream> openSource, Action<StockBaselineFile>? afterFile, CancellationToken ct)
    {
        using (Stream metadata = archive.CreateEntry(StockBaselineManifest.MANIFEST_PATH, CompressionLevel.NoCompression).Open())
            await metadata.WriteAsync(manifest, ct);
        foreach (string directory in expected.Directories)
        {
            ct.ThrowIfCancellationRequested();
            IZipArchiveEntry entry = archive.CreateEntry(directory + "/", CompressionLevel.NoCompression);
            if (expected.HasUnixMetadata)
                entry.ExternalAttributes = ((0x4000 | expected.UnixDirectoryModes[directory]) << 16) | 0x10;
        }
        var buffer = new byte[_limits.BufferBytes];
        var compression = _compressEntries ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
        foreach (StockBaselineFile file in expected.Files)
        {
            ct.ThrowIfCancellationRequested();
            using (Stream source = openSource(file))
            {
                IZipArchiveEntry entry = archive.CreateEntry(file.Path, compression);
                if (file.UnixMode is not null)
                    entry.ExternalAttributes = (0x8000 | file.UnixMode.Value) << 16;
                using Stream content = entry.Open();
                await CopyVerifiedAsync(source, content, file, buffer, ct);
            }
            afterFile?.Invoke(file);
        }
    }

    private void ValidateRecoveryPath(string path)
    {
        if (_recoveryPaths is null)
            return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Existing-backup recovery requires the Windows native path contract.");
        _recoveryPaths.ValidateControlledPath(path);
    }

    private long EstimateArchiveBytes(StockBaselineManifest expected, byte[] manifest)
    {
        long bytes = checked((_compressEntries ? 0 : expected.TotalBytes) + manifest.Length + 256);
        long directoryBytes = 0;
        foreach (string name in expected.Files.Select(file => file.Path)
                     .Concat(expected.Directories.Select(directory => directory + "/"))
                     .Append(StockBaselineManifest.MANIFEST_PATH))
        {
            int length = Encoding.UTF8.GetByteCount(name);
            bytes = checked(bytes + 256 + 2L * length);
            directoryBytes = checked(directoryBytes + 46 + 32 + length);
        }
        if (bytes > _limits.MaxArchiveBytes || directoryBytes > _limits.MaxCentralDirectoryBytes)
            throw new InvalidDataException("Archive would exceed the ZIP byte budget.");
        return bytes;
    }

    private Stream ArchiveOutput(Stream output, string parent)
    {
        if (!_compressEntries)
            return output;
        long available = _availableSpace(parent) ?? throw new IOException("Unknown compressed stock archive staging space.");
        if (available <= _limits.DiskReserveBytes)
            throw new IOException("Insufficient compressed stock archive staging space.");
        long budget = Math.Min(_limits.MaxArchiveBytes, available - _limits.DiskReserveBytes);
        return new ArchiveWriteBudget(output, budget);
    }

    private sealed class ArchiveWriteBudget(Stream output, long budget) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => output.CanSeek;
        public override bool CanWrite => output.CanWrite;
        public override long Length => output.Length;
        public override long Position { get => output.Position; set => output.Position = value; }
        public override void Flush() => output.Flush();
        public override long Seek(long offset, SeekOrigin origin) => output.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void SetLength(long value)
        {
            RequireRange(0, value);
            output.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireRange(Position, buffer.Length);
            output.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            RequireRange(Position, buffer.Length);
            return output.WriteAsync(buffer, ct);
        }

        private void RequireRange(long start, long count)
        {
            if (count < 0 || start < 0 || count > budget || start > budget - count)
                throw new IOException("Compressed stock archive exceeds its staging space or archive byte budget.");
        }
    }
}
