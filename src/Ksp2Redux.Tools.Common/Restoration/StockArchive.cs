using System.Buffers.Binary;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Wrappers;
using Ksp2Redux.Tools.Common.Transactions;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.Restoration;

/// <summary>
/// Create and verify full stock archives, and restore verified bytes into new staging trees.
/// </summary>
/// <remarks>
/// Expected hashes are supplied by the caller, never inferred from installed bytes.
/// Authentication and in-place install transactions belong to higher layers.
/// Operations require exclusive caller control of their staging parents and a stable source tree.
/// Path checks reject existing links but are not a sandbox against hostile concurrent directory replacement.
/// ZIP64 is supplied by the canonical ZIP service. Compression preserves the same stock manifest and byte checks.
/// </remarks>
public partial class StockArchive
{
    private readonly IFileSystem _fileSystem;
    private readonly IZipFileService _zipFileService;
    private readonly StockArchiveLimits _limits;
    private readonly Func<string, long?> _availableSpace;
    private readonly bool _compressEntries;

    /// <summary>
    /// Construct the shared archive primitive using the canonical filesystem and ZIP abstractions.
    /// </summary>
    /// <param name="fileSystem">The filesystem used for all disk access.</param>
    /// <param name="zipFileService">The ZIP service used for stream-backed archive access.</param>
    /// <param name="limits">The operation budgets, or the standard stock archive budgets.</param>
    /// <param name="availableSpace">The free-space query, or the filesystem drive query.</param>
    /// <param name="compressEntries">True to compress stock file entries in new archives.</param>
    public StockArchive(IFileSystem fileSystem, IZipFileService zipFileService,
        StockArchiveLimits? limits = null, Func<string, long?>? availableSpace = null, bool compressEntries = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(zipFileService);
        _fileSystem = fileSystem;
        _zipFileService = zipFileService;
        _limits = limits ?? new StockArchiveLimits();
        _availableSpace = availableSpace ?? GetAvailableSpace;
        _compressEntries = compressEntries;
        _recoveryPaths = fileSystem is RealFileSystem ? new InstallTransactionEngine(fileSystem, _availableSpace) : null;
    }

    /// <summary>
    /// Create a full archive beside its destination, verify all bytes, then publish with one rename.
    /// </summary>
    /// <remarks>
    /// Unknown source files are untouched and excluded. Existing archives remain until publication.
    /// Failure or cancellation deletes owned staging files and never deletes the destination archive.
    /// Native publication uses one same-filesystem rename with no copy or delete fallback.
    /// Flush-to-disk precedes verification and publication. Crash durability of directory metadata
    /// depends on the filesystem and is not an install transaction journal.
    /// Cleanup failures are attached to the original exception's Data as StockArchiveCleanupFailure
    /// and StockArchiveStagingPath so the caller can report or recover retained staging data.
    /// </remarks>
    /// <param name="sourceRoot">The stable tree containing the expected baseline bytes.</param>
    /// <param name="archivePath">The archive destination with an existing parent directory.</param>
    /// <param name="expected">The independently supplied expected baseline contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The task that completes after verified publication.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when paths, input bytes or archive metadata differ from the contract or exceed a budget.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown when disk access, space checks, source stability or native publication fails.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when cancellation is requested before publication.
    /// </exception>
    public Task CreateAsync(string sourceRoot, string archivePath, StockBaselineManifest expected,
        CancellationToken ct = default) => CreateCoreAsync(sourceRoot, archivePath, expected, false, ct);

    /// <summary>
    /// Creates a verified stock archive at an absent destination and returns the byte pin captured before publication.
    /// </summary>
    /// <param name="sourceRoot">The stable tree containing the expected baseline bytes.</param>
    /// <param name="archivePath">The archive destination with an existing parent directory.</param>
    /// <param name="expected">The independently supplied baseline contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The verified archive length and hash, with the publication path.</returns>
    public async Task<StockBaselineFile> CreateWithPinAsync(string sourceRoot, string archivePath,
        StockBaselineManifest expected, CancellationToken ct = default) =>
        (await CreateCoreAsync(sourceRoot, archivePath, expected, true, ct).ConfigureAwait(false))!;

    private async Task<StockBaselineFile?> CreateCoreAsync(string sourceRoot, string archivePath,
        StockBaselineManifest expected, bool capturePin, CancellationToken ct)
    {
        byte[] manifestBytes = ValidateContract(expected);
        ct.ThrowIfCancellationRequested();
        string root = DiskPath(sourceRoot);
        string destination = DiskPath(archivePath);
        EnsureSafePath(root, true);
        string parent = _fileSystem.Path.GetDirectoryName(destination)!;
        EnsureSafePath(parent, true);
        EnsureSafePath(destination, false, true);
        if (capturePin)
            EnsureAbsent(destination);
        foreach (StockBaselineFile file in expected.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (ContainedPath(root, file.Path).Equals(destination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The archive destination is an expected source file.");
            EnsureSafePath(ContainedPath(root, file.Path), false);
        }
        foreach (string directory in expected.Directories)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSafePath(ContainedPath(root, directory), true);
        }
        EnsureSourceCaseUniqueness(root, expected, ct);

        // Compressed creation caps actual output instead of guessing how well the stock files compress.
        long estimatedBytes = EstimateArchiveBytes(expected, manifestBytes);
        EnsureSpace(parent, estimatedBytes);

        string staging = _fileSystem.Path.Combine(parent, $".stock-archive-{Guid.NewGuid():N}.tmp");
        bool ownsStaging = false;
        try
        {
            await using (FileSystemStream output = _fileSystem.FileStream.New(staging, FileMode.CreateNew,
                             FileAccess.ReadWrite, FileShare.None, _limits.BufferBytes, FileOptions.Asynchronous))
            {
                ownsStaging = true;
                using (IZipArchive archive = _zipFileService.NewArchive(ArchiveOutput(output, parent), ZipArchiveMode.Create, true))
                {
                    foreach (string directory in expected.Directories)
                    {
                        if (expected.HasUnixMetadata)
                            RequireNativeUnixMode(ContainedPath(root, directory), expected.UnixDirectoryModes[directory]);
                    }
                    var snapshots = new Dictionary<string, (long Length, DateTime LastWrite)>();
                    await WriteContentsAsync(archive, expected, manifestBytes, file =>
                    {
                        string path = ContainedPath(root, file.Path);
                        EnsureSafePath(path, false);
                        RequireNativeUnixMode(path, file.UnixMode);
                        var before = _fileSystem.FileInfo.New(path);
                        if (before.Length != file.Length)
                            throw new InvalidDataException($"Source length differs from the baseline: '{file.Path}'.");
                        snapshots.Add(file.Path, (before.Length, before.LastWriteTimeUtc));
                        FileSystemStream stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read,
                            FileShare.Read, _limits.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        try
                        {
                            EnsureSafePath(path, false);
                            return stream;
                        }
                        catch
                        {
                            stream.Dispose();
                            throw;
                        }
                    }, file =>
                    {
                        string path = ContainedPath(root, file.Path);
                        var after = _fileSystem.FileInfo.New(path);
                        EnsureSafePath(path, false);
                        if (after.Length != snapshots[file.Path].Length || after.LastWriteTimeUtc != snapshots[file.Path].LastWrite)
                            throw new IOException($"Source changed while being archived: '{file.Path}'.");
                    }, ct);
                }
                if (output.Length > _limits.MaxArchiveBytes)
                    throw new InvalidDataException("Created ZIP exceeds the byte budget.");
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            StockBaselineFile? pin = null;
            if (capturePin)
                pin = await VerifyAndPinAsync(staging, expected, ct);
            else
                await VerifyAsync(staging, expected, ct);
            EnsureSourceCaseUniqueness(root, expected, ct);
            ct.ThrowIfCancellationRequested();
            EnsureSafePath(parent, true);
            EnsureSafePath(destination, false, true);
            PublishFile(_fileSystem, staging, destination, !capturePin && _fileSystem.File.Exists(destination));
            ownsStaging = false;
            return pin is null ? null : pin with { Path = destination };
        }
        catch (Exception failure)
        {
            if (ownsStaging)
                CleanupFile(staging, failure);
            throw;
        }
    }

    /// <summary>
    /// Verify the exact manifest contract, entry set and every decompressed content byte.
    /// </summary>
    /// <param name="archivePath">The archive path.</param>
    /// <param name="expected">The caller's expected contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The task that completes only after full verification.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the complete archive does not match the expected contract and resource budgets.
    /// </exception>
    public async Task VerifyAsync(string archivePath, StockBaselineManifest expected, CancellationToken ct = default)
    {
        string path = DiskPath(archivePath);
        EnsureSafePath(path, false);
        await using FileSystemStream input = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, _limits.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureSafePath(path, false);
        await VerifyAsync(input, expected, ct);
    }

    /// <summary>
    /// Verifies the stock entries and hashes the complete archive through the same read-only handle.
    /// </summary>
    /// <param name="archivePath">The archive path.</param>
    /// <param name="expected">The independently supplied baseline contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The path, length and hash of the verified archive.</returns>
    public async Task<StockBaselineFile> VerifyAndPinAsync(string archivePath, StockBaselineManifest expected,
        CancellationToken ct = default)
    {
        string path = DiskPath(archivePath);
        EnsureSafePath(path, false);
        await using FileSystemStream input = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, _limits.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureSafePath(path, false);
        await VerifyAsync(input, expected, ct).ConfigureAwait(false);
        byte[] hash = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        return new StockBaselineFile(path, input.Length, Convert.ToHexString(hash));
    }

    /// <summary>
    /// Verify a caller-owned seekable archive stream without disposing it or changing its position.
    /// </summary>
    /// <param name="input">The readable, seekable stream containing the whole archive from offset zero.</param>
    /// <param name="expected">The caller's expected contract.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The task that completes only after full verification.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the complete archive does not match the expected contract and resource budgets.
    /// </exception>
    public async Task VerifyAsync(Stream input, StockBaselineManifest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] manifestBytes = ValidateContract(expected);
        ct.ThrowIfCancellationRequested();
        ValidateArchiveStream(input, expected, ct);
        long originalPosition = input.Position;
        try
        {
            ct.ThrowIfCancellationRequested();
            input.Position = 0;
            using IZipArchive archive = _zipFileService.NewArchive(input, ZipArchiveMode.Read, true);
            await VerifyOpenArchiveAsync(archive, expected, manifestBytes, ct);
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    /// <summary>
    /// Restore into an owned sibling tree, verify the restored disk bytes and publish to a new root.
    /// </summary>
    /// <remarks>
    /// The requested root must not exist, even as an empty directory. Existing files and user trees
    /// are never overwritten. Partial owned staging trees are removed on failure or cancellation.
    /// Cleanup failures are attached to the original exception's Data as StockArchiveCleanupFailure
    /// and StockArchiveStagingPath.
    /// </remarks>
    /// <param name="archivePath">The full stock archive path.</param>
    /// <param name="expected">The caller's expected baseline contract.</param>
    /// <param name="newRoot">The absent destination directory with an existing parent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The task that completes after verified staging-tree publication.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when archive or restored bytes, metadata or paths fail verification.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown when the destination exists or disk access, space checks or publication fails.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when cancellation is requested before staging-tree publication.
    /// </exception>
    public async Task RestoreToNewDirectoryAsync(string archivePath, StockBaselineManifest expected,
        string newRoot, CancellationToken ct = default)
    {
        byte[] manifestBytes = ValidateContract(expected);
        ct.ThrowIfCancellationRequested();
        string archivePathFull = DiskPath(archivePath);
        string destination = DiskPath(newRoot);
        string parent = _fileSystem.Path.GetDirectoryName(destination)!;
        EnsureSafePath(parent, true);
        EnsureAbsent(destination);
        EnsureSafePath(archivePathFull, false);
        await using FileSystemStream input = _fileSystem.FileStream.New(archivePathFull, FileMode.Open,
            FileAccess.Read, FileShare.Read, _limits.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureSafePath(archivePathFull, false);
        ValidateArchiveStream(input, expected, ct);
        using IZipArchive archive = _zipFileService.NewArchive(input, ZipArchiveMode.Read, true);
        Dictionary<string, IZipArchiveEntry> entries = await VerifyOpenArchiveAsync(archive, expected, manifestBytes, ct);
        long allocationOverhead = checked(4096L * (expected.Files.Count + expected.Directories.Count + 1));
        EnsureSpace(parent, checked(expected.TotalBytes + allocationOverhead));
        string staging = _fileSystem.Path.Combine(parent, $".stock-restore-{Guid.NewGuid():N}");
        bool ownsStaging = false;
        try
        {
            EnsureAbsent(staging);
            CreateDirectory(_fileSystem, staging);
            ownsStaging = true;
            foreach (string directory in expected.Directories)
            {
                ct.ThrowIfCancellationRequested();
                string path = ContainedPath(staging, directory);
                EnsureSafePath(_fileSystem.Path.GetDirectoryName(path)!, true);
                CreateDirectory(_fileSystem, path);
                SetNativeUnixMode(path, expected.HasUnixMetadata ? expected.UnixDirectoryModes[directory] : null);
                FlushLinuxDirectory(_fileSystem, _fileSystem.Path.GetDirectoryName(path)!);
                EnsureSafePath(path, true);
            }
            var buffer = new byte[_limits.BufferBytes];
            foreach (StockBaselineFile file in expected.Files)
            {
                ct.ThrowIfCancellationRequested();
                string path = ContainedPath(staging, file.Path);
                EnsureSafePath(_fileSystem.Path.GetDirectoryName(path)!, true);
                EnsureAbsent(path);
                using Stream content = entries[file.Path].Open();
                await using FileSystemStream output = _fileSystem.FileStream.New(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, _limits.BufferBytes, FileOptions.Asynchronous);
                await CopyVerifiedAsync(content, output, file, buffer, ct);
                await output.FlushAsync(ct);
                output.Flush(true);
                SetNativeUnixMode(path, file.UnixMode);
                FlushLinuxDirectory(_fileSystem, _fileSystem.Path.GetDirectoryName(path)!);
            }
            foreach (StockBaselineFile file in expected.Files)
            {
                string path = ContainedPath(staging, file.Path);
                EnsureSafePath(path, false);
                RequireNativeUnixMode(path, file.UnixMode);
                await using FileSystemStream content = _fileSystem.FileStream.New(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read, _limits.BufferBytes, FileOptions.Asynchronous);
                await CopyVerifiedAsync(content, null, file, buffer, ct);
            }
            ct.ThrowIfCancellationRequested();
            EnsureSafePath(parent, true);
            EnsureAbsent(destination);
            if (_fileSystem is RealFileSystem && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                PublishFile(_fileSystem, staging, destination, false);
            else
                _fileSystem.Directory.Move(staging, destination);
            ownsStaging = false;
        }
        catch (Exception failure)
        {
            if (ownsStaging)
            {
                try
                {
                    DeleteOwnedTree(staging);
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

    private byte[] ValidateContract(StockBaselineManifest expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.HasUnixMetadata && _fileSystem is RealFileSystem &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("Unix archive metadata requires a verified native host adapter.");
        _limits.Validate(expected);
        byte[] bytes = expected.ToJson(_limits.MaxManifestBytes);
        if (bytes.Length > _limits.MaxManifestBytes)
            throw new InvalidDataException("Manifest exceeds the metadata budget.");
        return bytes;
    }

    private void ValidateArchiveStream(Stream input, StockBaselineManifest expected, CancellationToken ct)
    {
        if (!input.CanRead || !input.CanSeek)
            throw new ArgumentException("Archive verification requires a readable seekable stream.", nameof(input));
        if (input.Length > _limits.MaxArchiveBytes)
            throw new InvalidDataException("ZIP exceeds the archive byte budget.");
        long originalPosition = input.Position;
        try
        {
            CheckZipDirectoryBudget(input, checked(expected.Files.Count + expected.Directories.Count + 1), ct);
        }
        catch (EndOfStreamException failure)
        {
            throw new InvalidDataException("ZIP metadata is truncated.", failure);
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    private void CheckZipDirectoryBudget(Stream input, int? expectedEntries, CancellationToken ct)
    {
        // Bound the directory before ZipArchive eagerly allocates every central-directory entry.
        // Check record boundaries too, so a false EOCD count or size cannot bypass the allocation cap.
        int tailLength = (int)Math.Min(input.Length, 22 + ushort.MaxValue);
        var tail = new byte[tailLength];
        input.Position = input.Length - tailLength;
        input.ReadExactly(tail);
        int endIndex = -1;
        for (int index = tailLength - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) == 0x06054B50 &&
                index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, 2)) == tailLength)
            {
                endIndex = index;
                break;
            }
        }
        if (endIndex < 0)
            throw new InvalidDataException("ZIP end-of-directory record is missing or truncated.");
        ReadOnlySpan<byte> end = tail.AsSpan(endIndex);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != BinaryPrimitives.ReadUInt16LittleEndian(end[10..]))
            throw new InvalidDataException("Multi-volume stock archives are not supported.");
        ulong entryCount = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        ulong directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        ulong directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        long directoryEnd = input.Length - tailLength + endIndex;
        bool needsZip64 = entryCount == ushort.MaxValue || directoryBytes == uint.MaxValue || directoryOffset == uint.MaxValue;
        if (directoryEnd >= 20)
        {
            var locator = new byte[20];
            input.Position = directoryEnd - 20;
            input.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == 0x07064B50)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                    throw new InvalidDataException("Multi-volume ZIP64 stock archives are invalid.");
                ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                if (zip64Offset > (ulong)(directoryEnd - 20) || (ulong)(directoryEnd - 20) - zip64Offset < 56)
                    throw new InvalidDataException("Invalid ZIP64 end record offset.");
                input.Position = (long)zip64Offset;
                var zip64 = new byte[56];
                input.ReadExactly(zip64);
                ulong recordBytes = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(4));
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064B50 || recordBytes < 44 ||
                    recordBytes > (ulong)_limits.MaxCentralDirectoryBytes ||
                    recordBytes != (ulong)(directoryEnd - 20) - zip64Offset - 12 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0 ||
                    BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(24)) != BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32)))
                    throw new InvalidDataException("Invalid ZIP64 end-of-directory record.");
                ulong zip64Entries = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
                ulong zip64DirectoryBytes = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
                ulong zip64DirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
                if ((entryCount != ushort.MaxValue && entryCount != zip64Entries) ||
                    (directoryBytes != uint.MaxValue && directoryBytes != zip64DirectoryBytes) ||
                    (directoryOffset != uint.MaxValue && directoryOffset != zip64DirectoryOffset))
                    throw new InvalidDataException("Classic and ZIP64 directory records disagree.");
                entryCount = zip64Entries;
                directoryBytes = zip64DirectoryBytes;
                directoryOffset = zip64DirectoryOffset;
                directoryEnd = (long)zip64Offset;
                needsZip64 = false;
            }
        }
        if (needsZip64 || (expectedEntries is { } count && entryCount != (ulong)count) || entryCount > (ulong)_limits.MaxEntries ||
            directoryBytes > (ulong)_limits.MaxCentralDirectoryBytes || directoryOffset > (ulong)directoryEnd ||
            directoryBytes != (ulong)directoryEnd - directoryOffset)
            throw new InvalidDataException("ZIP directory differs from the expected entry set or resource budget.");

        input.Position = (long)directoryOffset;
        var header = new byte[46];
        for (int index = 0; index < checked((int)entryCount); index++)
        {
            ct.ThrowIfCancellationRequested();
            if (directoryEnd - input.Position < header.Length)
                throw new InvalidDataException("ZIP central-directory records are truncated.");
            input.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 0)
                throw new InvalidDataException("Invalid ZIP central-directory record.");
            long extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) +
                              BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) +
                              BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (extraBytes > directoryEnd - input.Position)
                throw new InvalidDataException("ZIP central-directory record escapes the metadata budget.");
            input.Position += extraBytes;
        }
        if (input.Position != directoryEnd)
            throw new InvalidDataException("ZIP contains undeclared central-directory records.");
    }

    private async Task<Dictionary<string, IZipArchiveEntry>> VerifyOpenArchiveAsync(IZipArchive archive,
        StockBaselineManifest expected, byte[] manifestBytes, CancellationToken ct)
    {
        var entries = InspectCanonicalEntries(archive, expected, manifestBytes, ct);
        var buffer = new byte[_limits.BufferBytes];
        foreach (StockBaselineFile file in expected.Files)
        {
            using Stream content = entries[file.Path].Open();
            await CopyVerifiedAsync(content, null, file, buffer, ct);
        }
        return entries;
    }

    // The caller holds the exact, previously fully verified archive. Each opened file must still
    // be checked against its independent contract when staging it.
    internal PinnedEntries OpenPinnedEntries(Stream input, StockBaselineManifest expected, CancellationToken ct)
    {
        byte[] manifestBytes = ValidateContract(expected);
        ValidateArchiveStream(input, expected, ct);
        input.Position = 0;
        IZipArchive archive = _zipFileService.NewArchive(input, ZipArchiveMode.Read, true);
        try
        {
            return new PinnedEntries(archive, InspectCanonicalEntries(archive, expected, manifestBytes, ct));
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    internal sealed class PinnedEntries(IZipArchive archive, Dictionary<string, IZipArchiveEntry> entries) : IDisposable
    {
        internal Stream Open(StockBaselineFile file) => entries[file.Path].Open();
        public void Dispose() => archive.Dispose();
    }

    private Dictionary<string, IZipArchiveEntry> InspectCanonicalEntries(IZipArchive archive,
        StockBaselineManifest expected, byte[] manifestBytes, CancellationToken ct)
    {
        IReadOnlyList<IZipArchiveEntry> allEntries = archive.Entries;
        if (allEntries.Count > _limits.MaxEntries ||
            allEntries.Count != expected.Files.Count + expected.Directories.Count + 1)
            throw new InvalidDataException("ZIP entry set does not match the complete expected baseline.");
        var entries = new Dictionary<string, IZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(expected.Files.Select(file => file.Path)
            .Concat(expected.Directories.Select(directory => directory + "/"))
            .Append(StockBaselineManifest.MANIFEST_PATH), StringComparer.Ordinal);
        foreach (IZipArchiveEntry entry in allEntries)
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.FullName;
            bool isDirectory = name.EndsWith('/');
            string path = isDirectory ? name[..^1] : name;
            if (name != StockBaselineManifest.MANIFEST_PATH && StockBaselineManifest.NormalizePath(path) != path)
                throw new InvalidDataException($"Noncanonical ZIP path '{name}'.");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (unixType != 0 && unixType != (isDirectory ? 0x4000 : 0x8000)) ||
                (!isDirectory && (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0))
                throw new InvalidDataException($"ZIP link or special file '{name}' is invalid.");
            if (!entries.TryAdd(path, entry) || !required.Remove(name))
                throw new InvalidDataException($"Unexpected, duplicate or conflicting ZIP path '{name}'.");
            if (entry.Length < 0 || (isDirectory && entry.Length != 0))
                throw new InvalidDataException($"Invalid ZIP entry length for '{name}'.");
        }
        if (required.Count != 0)
            throw new InvalidDataException("ZIP is missing expected entries.");
        IZipArchiveEntry metadata = entries[StockBaselineManifest.MANIFEST_PATH];
        if (metadata.Length != manifestBytes.Length)
            throw new InvalidDataException("Archive manifest differs from the expected contract.");
        using (Stream content = metadata.Open())
        {
            var actual = new byte[manifestBytes.Length];
            ct.ThrowIfCancellationRequested();
            content.ReadExactly(actual);
            if (content.ReadByte() != -1 || !actual.AsSpan().SequenceEqual(manifestBytes))
                throw new InvalidDataException("Archive manifest differs from the expected contract.");
        }
        foreach (string directory in expected.Directories)
        {
            ct.ThrowIfCancellationRequested();
            if (expected.HasUnixMetadata && (((uint)entries[directory].ExternalAttributes >> 16) & 0xfff) != expected.UnixDirectoryModes[directory])
                throw new InvalidDataException($"ZIP directory permissions differ from the contract: '{directory}'.");
            using Stream content = entries[directory].Open();
            if (content.ReadByte() != -1)
                throw new InvalidDataException($"ZIP directory contains data: '{directory}'.");
        }
        foreach (StockBaselineFile file in expected.Files)
        {
            ct.ThrowIfCancellationRequested();
            IZipArchiveEntry entry = entries[file.Path];
            if (file.UnixMode is not null && (((uint)entry.ExternalAttributes >> 16) & 0xfff) != file.UnixMode.Value)
                throw new InvalidDataException($"ZIP permission metadata differs from the contract: '{file.Path}'.");
            if (entry.Length != file.Length)
                throw new InvalidDataException($"ZIP length differs from the baseline: '{file.Path}'.");
        }
        return entries;
    }

    private static async Task CopyVerifiedAsync(Stream input, Stream? output, StockBaselineFile expected,
        byte[] buffer, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = expected.Length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
            if (read == 0)
                throw new InvalidDataException($"Content is truncated: '{expected.Path}'.");
            hash.AppendData(buffer, 0, read);
            if (output is not null)
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
            throw new InvalidDataException($"Content exceeds expected length: '{expected.Path}'.");
        if (Convert.ToHexString(hash.GetHashAndReset()) != expected.Sha256)
            throw new InvalidDataException($"SHA-256 differs from the baseline: '{expected.Path}'.");
    }

    private string DiskPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedInput = path.Replace('\\', '/');
        if (normalizedInput.StartsWith("//?/", StringComparison.Ordinal) ||
            normalizedInput.StartsWith("//./", StringComparison.Ordinal) || normalizedInput.Split('/').Contains(".."))
            throw new InvalidDataException("Device namespace and parent traversal disk paths are invalid.");
        string full = _fileSystem.Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal) || full.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new InvalidDataException("Device namespace disk paths are invalid.");
        string root = _fileSystem.Path.GetPathRoot(full)!;
        foreach (string segment in full[root.Length..].Replace('\\', '/').TrimEnd('/').Split('/'))
        {
            if (segment.Length != 0)
                StockBaselineManifest.ValidateSegment(segment);
        }
        return _fileSystem.Path.TrimEndingDirectorySeparator(full);
    }

    private string ContainedPath(string root, string relative)
    {
        string path = DiskPath(_fileSystem.Path.Combine(root, relative.Replace('/', _fileSystem.Path.DirectorySeparatorChar)));
        string basePath = _fileSystem.Path.TrimEndingDirectorySeparator(root);
        string prefix = basePath.EndsWith(_fileSystem.Path.DirectorySeparatorChar)
            ? basePath
            : basePath + _fileSystem.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stock path escapes its root.");
        return path;
    }

    private void EnsureSafePath(string fullPath, bool isDirectory, bool allowMissingLeaf = false)
    {
        string root = _fileSystem.Path.GetPathRoot(fullPath)!;
        string current = root;
        FileAttributes rootAttributes = _fileSystem.File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Reparse root '{root}' is invalid.");
        string[] segments = fullPath[root.Length..].Split(_fileSystem.Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            current = _fileSystem.Path.Combine(current, segment);
            bool isLeaf = index == segments.Length - 1;
            if (isLeaf && allowMissingLeaf)
            {
                string parent = _fileSystem.Path.GetDirectoryName(current)!;
                string[] matches = _fileSystem.Directory.EnumerateFileSystemEntries(parent)
                    .Where(path => _fileSystem.Path.GetFileName(path).Equals(segment, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1 || (matches.Length == 1 && matches[0] != current))
                    throw new InvalidDataException($"Case-colliding disk entries below '{parent}'.");
                if (matches.Length == 0)
                    return;
            }
            FileAttributes attributes = _fileSystem.File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != (!isLeaf || isDirectory))
                throw new InvalidDataException($"Link or conflicting disk path '{current}' is invalid.");
        }
        if (_fileSystem is RealFileSystem && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            _ = LinuxIdentity(fullPath);
    }

    private void RequireNativeUnixMode(string path, int? mode)
    {
        if (mode is not null && !UnixPermissionsMatch(_fileSystem, path, mode.Value))
            throw new InvalidDataException($"Unix permissions differ from the expected archive source: '{path}'.");
    }

    private void SetNativeUnixMode(string path, int? mode)
    {
        if (mode is not null)
            ApplyUnixPermissions(_fileSystem, path, mode.Value);
    }

    private void EnsureSourceCaseUniqueness(string root, StockBaselineManifest expected, CancellationToken ct)
    {
        var requiredChildren = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in expected.Files.Select(file => file.Path).Concat(expected.Directories))
        {
            ct.ThrowIfCancellationRequested();
            int slash = relative.LastIndexOf('/');
            string parent = slash < 0 ? root : ContainedPath(root, relative[..slash]);
            string name = relative[(slash + 1)..];
            if (!requiredChildren.TryGetValue(parent, out HashSet<string>? children))
            {
                children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                requiredChildren.Add(parent, children);
            }
            children.Add(name);
        }
        foreach (var parent in requiredChildren)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSafePath(parent.Key, true);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in _fileSystem.Directory.EnumerateFileSystemEntries(parent.Key))
            {
                string name = _fileSystem.Path.GetFileName(path);
                if (parent.Value.Contains(name) && !seen.Add(name))
                    throw new InvalidDataException($"Case-colliding disk entries below '{parent.Key}'.");
            }
        }
    }

    private void EnsureAbsent(string path)
    {
        EnsureSafePath(path, false, true);
        if (_fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path))
            throw new IOException($"Restore staging destination already exists: '{path}'.");
    }

    private void EnsureSpace(string parent, long bytes)
    {
        long required = checked(bytes + _limits.DiskReserveBytes);
        long? available = _availableSpace(parent);
        if (available is null || available.Value < required)
            throw new IOException($"Insufficient or unknown staging space at '{parent}'. Required {required} bytes, available {available?.ToString() ?? "unknown"}.");
    }

    private long? GetAvailableSpace(string path)
    {
        string root = _fileSystem.Path.GetPathRoot(path)!;
        string query = root == "/" ? path : root;
        try
        {
            return _fileSystem.DriveInfo.New(query).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void CleanupFile(string path, Exception failure)
    {
        try
        {
            EnsureSafePath(path, false, true);
            _fileSystem.File.Delete(path);
        }
        catch (Exception cleanupFailure)
        {
            failure.Data["StockArchiveCleanupFailure"] = cleanupFailure;
            failure.Data["StockArchiveStagingPath"] = path;
        }
    }

    private void DeleteOwnedTree(string path)
    {
        EnsureSafePath(path, true);
        foreach (string entry in _fileSystem.Directory.EnumerateFileSystemEntries(path))
        {
            FileAttributes attributes = _fileSystem.File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                _fileSystem.File.Delete(entry);
            }
            else if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                _fileSystem.Directory.Delete(entry);
            }
            else
            {
                DeleteOwnedTree(entry);
            }
        }
        _fileSystem.Directory.Delete(path);
    }
}
