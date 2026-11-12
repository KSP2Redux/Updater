using System.Buffers.Binary;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Read-only protection reasons for an installation, without authenticating its backup or recovery plan.
/// </summary>
/// <param name="LaunchBlockReason">The pending-state reason blocking launch, or null.</param>
/// <param name="LegacyMutationBlockReason">The reason blocking legacy file writes, or null.</param>
public record InstallationState(string? LaunchBlockReason, string? LegacyMutationBlockReason);

/// <summary>
/// Shared boundary protecting installation evidence from legacy launcher operations.
/// </summary>
public interface IInstallationStateService
{
    /// <summary>
    /// Inspects explicit installation state without changing or repairing any file.
    /// </summary>
    /// <param name="installRoot">The installation folder.</param>
    /// <returns>The protection reasons for launch and legacy mutation.</returns>
    InstallationState Inspect(string installRoot);

    /// <summary>
    /// Rejects legacy mutations before they download, revert, purge or write installation content.
    /// </summary>
    /// <param name="installRoot">The installation folder.</param>
    /// <exception cref="IOException">Thrown when protected or unreadable state prevents mutation.</exception>
    void EnsureLegacyMutationAllowed(string installRoot);

    /// <summary>
    /// Rejects game launch while transaction state is pending or cannot be inspected.
    /// </summary>
    /// <param name="installRoot">The installation folder.</param>
    /// <exception cref="IOException">Thrown when pending state prevents launch.</exception>
    void EnsureLaunchAllowed(string installRoot);

    /// <summary>
    /// Check launch state while reserving the intended root against full-source installation.
    /// </summary>
    /// <param name="installRoot">The configured intended installation folder.</param>
    /// <param name="ct">Cancellation for reservation and owned-file verification.</param>
    /// <returns>The lease to hold through startup detection, or null for isolated legacy callers.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is observed before handoff.</exception>
    IDisposable? AcquireLaunchLease(string installRoot, CancellationToken ct = default);

    /// <summary>
    /// Rejects launcher replacement while any explicitly configured installation has pending state.
    /// </summary>
    /// <param name="configuration">The configured installation identities.</param>
    /// <exception cref="IOException">Thrown when pending state prevents launcher replacement.</exception>
    void EnsureSelfUpdateAllowed(LauncherConfig configuration);

    /// <summary>
    /// Checks persisted identities before the early self-update executable replacement stages.
    /// </summary>
    /// <param name="configurationPath">The launcher configuration file.</param>
    /// <exception cref="IOException">Thrown when configuration or pending state cannot safely be inspected.</exception>
    void EnsureSelfUpdateAllowedFromFile(string configurationPath);
}

/// <summary>
/// Inspects transaction presence and bounded ZIP metadata for all GUI and CLI mutation entry points.
/// </summary>
/// <param name="fileSystem">The host filesystem.</param>
/// <param name="zipFileService">The ZIP abstraction used after bounding its metadata.</param>
/// <param name="fullSource">The registered production coordinator, or null for isolated legacy callers.</param>
public class InstallationStateService(IFileSystem fileSystem, IZipFileService zipFileService,
    IFullSourceInstallationService? fullSource = null) : IInstallationStateService
{
    private const string ACTIVE_MARKER = ".redux-transaction.active";
    private const string ARCHIVE_NAME = "uninstall.zip";
    private const int MAX_ENTRIES = 100_000;
    private const int MAX_DIRECTORY_BYTES = 16 * 1024 * 1024;
    private const int MAX_CONFIGURATION_BYTES = 4 * 1024 * 1024;
    private const int MAX_ENTRY_NAME_CHARS = 4096;
    private const int MAX_PATH_SEGMENTS = 128;

    /// <inheritdoc />
    public InstallationState Inspect(string installRoot)
    {
        string? pending = InspectPending(installRoot);
        if (pending is not null)
            return new InstallationState(pending, pending);

        try
        {
            if (fullSource?.HasCatalog(installRoot) == true)
                return ProtectedArchive(installRoot);
            string manifestPath = fileSystem.Path.Combine(installRoot,
                StockBaselineManifest.MANIFEST_PATH.Replace('/', fileSystem.Path.DirectorySeparatorChar));
            if (Exists(manifestPath))
                return ProtectedArchive(installRoot);

            string archivePath = fileSystem.Path.Combine(installRoot, ARCHIVE_NAME);
            if (!Exists(archivePath))
                return new InstallationState(null, null);

            using Stream input = fileSystem.File.Open(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            CheckDirectoryBudget(input);
            input.Position = 0;
            using var archive = zipFileService.NewArchive(input, ZipArchiveMode.Read, true);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long inspectedNameChars = 0;
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                string path = name.TrimEnd('/');
                bool isDirectory = name.EndsWith('/');
                // Neither manifest content nor its declared payload length is read or trusted.
                if (string.Equals(path, StockBaselineManifest.MANIFEST_PATH, StringComparison.OrdinalIgnoreCase))
                    return ProtectedArchive(installRoot);
                if (name.Length > MAX_ENTRY_NAME_CHARS)
                    throw new InvalidDataException("An archive entry name exceeds the inspection limit.");
                string[] segments = path.Split('/');
                if (segments.Length > MAX_PATH_SEGMENTS)
                    throw new InvalidDataException("An archive path exceeds the inspection depth limit.");
                if (!names.Add(path) || path.Length == 0 || fileSystem.Path.IsPathRooted(name) ||
                    segments.Any(part => part is "" or "." or ".."))
                    throw new InvalidDataException("The archive contains ambiguous entry names.");
                if ((isDirectory && files.Contains(path)) || (!isDirectory && directories.Contains(path)))
                    throw new InvalidDataException("The archive contains conflicting file and directory names.");
                if (isDirectory)
                    directories.Add(path);
                else
                    files.Add(path);
                inspectedNameChars += path.Length;
                int separator = path.LastIndexOf('/');
                while (separator >= 0)
                {
                    string parent = path[..separator];
                    inspectedNameChars += parent.Length;
                    if (inspectedNameChars > MAX_DIRECTORY_BYTES)
                        throw new InvalidDataException("Archive name inspection exceeds its resource limit.");
                    if (files.Contains(parent))
                        throw new InvalidDataException("An archive file is also the parent of another entry.");
                    directories.Add(parent);
                    separator = parent.LastIndexOf('/');
                }
                if (inspectedNameChars > MAX_DIRECTORY_BYTES)
                    throw new InvalidDataException("Archive name inspection exceeds its resource limit.");
                // Check local entry headers and supported compression without decompressing payloads.
                using Stream content = entry.Open();
            }
            return new InstallationState(null, null);
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return new InstallationState(null,
                $"The backup at '{installRoot}' could not be safely inspected: {ex.Message} " +
                "Legacy changes and boot.config writes are blocked. The backup has been preserved.");
        }
    }

    /// <inheritdoc />
    public void EnsureLegacyMutationAllowed(string installRoot)
    {
        fullSource?.EnsureReady(installRoot);
        if (Inspect(installRoot).LegacyMutationBlockReason is { } reason)
            throw new IOException(reason);
    }

    /// <inheritdoc />
    public void EnsureLaunchAllowed(string installRoot)
    {
        fullSource?.EnsureReady(installRoot);
        if (InspectPending(installRoot) is { } reason)
            throw new IOException(reason);
    }

    /// <inheritdoc />
    public IDisposable? AcquireLaunchLease(string installRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IDisposable? lease = fullSource?.AcquireLaunchLease(installRoot, ct);
        try
        {
            if (InspectPending(installRoot) is { } reason)
                throw new IOException(reason);
            ct.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void EnsureSelfUpdateAllowed(LauncherConfig configuration)
    {
        if (configuration.Ksp2Installs is null || configuration.Ksp2Installs.Any(entry => entry is null))
            throw new IOException("Launcher self-update is blocked because configured installation identities are unreadable.");
        foreach (string exePath in configuration.Ksp2Installs.Select(entry => entry.ExePath)
                     .Append(configuration.Ksp2InstallPath).Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!fileSystem.Path.IsPathFullyQualified(exePath))
                throw new IOException("Launcher self-update is blocked because a configured installation path is not absolute.");
            string? root = fileSystem.Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("Launcher self-update is blocked because a configured installation path is ambiguous.");
            fullSource?.EnsureReady(root);
            if (InspectPending(root) is { } reason)
                throw new IOException($"Launcher self-update is blocked. {reason}");
        }
    }

    /// <inheritdoc />
    public void EnsureSelfUpdateAllowedFromFile(string configurationPath)
    {
        try
        {
            if (!Exists(configurationPath))
                return;
            using Stream input = fileSystem.File.OpenRead(configurationPath);
            if (input.Length > MAX_CONFIGURATION_BYTES)
                throw new InvalidDataException("The launcher configuration exceeds the inspection limit.");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
                throw new InvalidDataException("The launcher configuration changed during inspection.");
            var configuration = JsonSerializer.Deserialize<LauncherConfig>(bytes)
                ?? throw new InvalidDataException("The launcher configuration is empty.");
            InspectCatalogsBeforeReplacement(configuration, configurationPath);
            EnsureSelfUpdateAllowed(configuration);
        }
        catch (Exception ex) when (IsInspectionFailure(ex) || ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IOException($"Launcher self-update is blocked: {ex.Message} No installation was changed.", ex);
        }
    }

    private string? InspectPending(string installRoot)
    {
        try
        {
            return Exists(fileSystem.Path.Combine(installRoot, ACTIVE_MARKER))
                ? $"An unfinished installation transaction is marked at '{installRoot}'. " +
                  "Recover it with the full-source installation coordinator before continuing. " +
                  "The marker and installation files have been preserved."
                : null;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            return $"Transaction state at '{installRoot}' could not be inspected: {ex.Message} " +
                   "Launch and changes are blocked. No installation files were changed.";
        }
    }

    private void InspectCatalogsBeforeReplacement(LauncherConfig configuration, string configurationPath)
    {
        if (configuration.Ksp2Installs is null || configuration.Ksp2Installs.Any(entry => entry is null))
            throw new InvalidDataException("Configured installation identities are unreadable.");
        string storage = fileSystem.Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidDataException("The launcher configuration storage is ambiguous.");
        foreach (Ksp2InstallEntry entry in configuration.Ksp2Installs)
        {
            string path = fileSystem.Path.Combine(storage, "full-source-v1", entry.Id.ToString("N"), "installation.json");
            if (!Exists(path))
                continue;
            // Early updater stages have no DI coordinator. Catalog presence is only a protection signal here.
            new Ksp2Redux.Tools.Common.Transactions.InstallTransactionEngine(fileSystem,
                validateFileStreams: fileSystem is RealFileSystem ? null : _ => { }).ValidateControlledPath(path);
            using Stream input = fileSystem.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 96 * 1024 * 1024)
                throw new InvalidDataException("The installation catalog exceeds the replacement inspection budget.");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            using JsonDocument envelope = JsonDocument.Parse(bytes);
            JsonElement record = envelope.RootElement;
            byte[] payload = record.GetProperty("Payload").GetBytesFromBase64();
            if (record.GetProperty("FormatVersion").GetInt32() != 1 ||
                record.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                record.EnumerateObject().Count() != 3 ||
                record.GetProperty("Sha256").GetString() != Convert.ToHexString(SHA256.HashData(payload)))
                throw new InvalidDataException("The installation catalog integrity or format is unknown. Evidence retained.");
            using JsonDocument catalog = JsonDocument.Parse(payload);
            JsonElement head = catalog.RootElement;
            string[] names = ["FormatVersion", "InstallationId", "InstallRoot", "StorageRoot", "Stock", "Ownership",
                "Release", "Archive", "RetainedFiles", "UnownedDirectories", "RetentionPolicy", "Pending", "LastResult", "LastTransaction"];
            if (head.GetProperty("FormatVersion").GetInt32() != 1 ||
                head.EnumerateObject().Count() != names.Length || head.EnumerateObject().Any(property => !names.Contains(property.Name)) ||
                head.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != head.EnumerateObject().Count() ||
                head.GetProperty("InstallationId").GetGuid() != entry.Id ||
                head.GetProperty("InstallRoot").GetString() != fileSystem.Path.GetDirectoryName(entry.ExePath) ||
                head.GetProperty("RetentionPolicy").GetString() is not ("retain-all-v1" or "retain-contracts-v1") ||
                head.GetProperty("Pending").ValueKind != JsonValueKind.Null)
                throw new IOException("Launcher replacement is blocked until installation recovery completes. Evidence retained.");
        }
    }

    private bool Exists(string path)
    {
        // File.Exists hides access failures and misses a directory at a reserved file path.
        try
        {
            fileSystem.File.GetAttributes(path);
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

    private static bool IsInspectionFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static InstallationState ProtectedArchive(string root) => new(null,
        $"The installation at '{root}' claims a full stock backup. Legacy changes and boot.config writes are blocked. " +
        "Use the full-source installation coordinator. The backup is preserved, but has not been authenticated or verified.");

    private static void CheckDirectoryBudget(Stream input)
    {
        // ZIP readers allocate the complete directory on first entry access. Validate its real
        // record boundaries before allowing that allocation, including archives with false counts.
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
            throw new InvalidDataException("The ZIP directory is missing or truncated.");
        ReadOnlySpan<byte> end = tail.AsSpan(endIndex);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != BinaryPrimitives.ReadUInt16LittleEndian(end[10..]))
            throw new InvalidDataException("Multi-volume ZIP metadata cannot be safely inspected.");
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
                ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1 ||
                    zip64Offset > (ulong)(directoryEnd - 20) || (ulong)(directoryEnd - 20) - zip64Offset < 56)
                    throw new InvalidDataException("The ZIP64 locator is invalid.");
                input.Position = (long)zip64Offset;
                var zip64 = new byte[56];
                input.ReadExactly(zip64);
                ulong recordBytes = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(4));
                if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064B50 || recordBytes < 44 ||
                    recordBytes > MAX_DIRECTORY_BYTES || recordBytes != (ulong)(directoryEnd - 20) - zip64Offset - 12 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0 ||
                    BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(24)) != BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32)))
                    throw new InvalidDataException("The ZIP64 directory is invalid.");
                ulong count64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
                ulong bytes64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
                ulong offset64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
                if ((entryCount != ushort.MaxValue && entryCount != count64) ||
                    (directoryBytes != uint.MaxValue && directoryBytes != bytes64) ||
                    (directoryOffset != uint.MaxValue && directoryOffset != offset64))
                    throw new InvalidDataException("The classic and ZIP64 directories disagree.");
                (entryCount, directoryBytes, directoryOffset, directoryEnd, needsZip64) =
                    (count64, bytes64, offset64, (long)zip64Offset, false);
            }
        }
        if (needsZip64 || entryCount > MAX_ENTRIES || directoryBytes > MAX_DIRECTORY_BYTES ||
            directoryOffset > (ulong)directoryEnd || directoryBytes != (ulong)directoryEnd - directoryOffset)
            throw new InvalidDataException("The ZIP metadata exceeds the inspection limit or is inconsistent.");

        input.Position = (long)directoryOffset;
        var header = new byte[46];
        for (ulong index = 0; index < entryCount; index++)
        {
            if (directoryEnd - input.Position < header.Length)
                throw new InvalidDataException("The ZIP directory records are truncated.");
            input.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 0)
                throw new InvalidDataException("The ZIP directory record is invalid.");
            long variableBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) +
                                 BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) +
                                 BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (variableBytes > directoryEnd - input.Position)
                throw new InvalidDataException("The ZIP directory record escapes its inspection limit.");
            input.Position += variableBytes;
        }
        if (input.Position != directoryEnd)
            throw new InvalidDataException("The ZIP contains undeclared directory records.");
    }
}
