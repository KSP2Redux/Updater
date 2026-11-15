using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>A zip of the player's saves and craft workspaces.</summary>
/// <param name="Path">The zip file.</param>
/// <param name="Created">When the backup was written, in local time.</param>
/// <param name="SizeBytes">The size of the zip file.</param>
public sealed record SaveBackup(string Path, DateTime Created, long SizeBytes);

public interface ISaveBackupService
{
    /// <summary>The folder the active install's backups are kept in, or null when its game data folder is unknown.</summary>
    string? BackupDirectory { get; }

    /// <summary>
    /// Zips the saves and craft workspaces from the game data folder, unless nothing changed since the newest backup
    /// of that folder. Only the newest backups of each game data folder are kept.
    /// </summary>
    /// <param name="installDir">The install about to change, used to find its game data folder. Null means the active install.</param>
    /// <param name="cancellationToken">Stops a backup of a large save folder.</param>
    /// <returns>The new backup, or null when there was nothing new to back up.</returns>
    SaveBackup? BackUp(string? installDir = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the backups of the active install's game data folder, newest first.</summary>
    IReadOnlyList<SaveBackup> List();

    /// <summary>
    /// Puts back the saves and workspaces from <paramref name="backup"/> for the active install. The current ones
    /// are backed up first, and folders the backup does not contain are left alone. The caller makes sure KSP2 is
    /// not running.
    /// </summary>
    /// <exception cref="InvalidDataException">The backup is damaged or holds files outside the save folders.</exception>
    /// <exception cref="InvalidOperationException">The backup belongs to another game data folder.</exception>
    void Restore(SaveBackup backup);
}

/// <summary>
/// Backs up the player's saves before the launcher changes the game, and restores them on request.
/// </summary>
public class SaveBackupService(
    IFileSystem fileSystem,
    IZipFileService zipFileService,
    IGameDataFolderService gameDataFolderService,
    IKsp2InstallService ksp2InstallService,
    ILauncherConfigService launcherConfigService) : ISaveBackupService
{
    // StockWorkspaces is the game's own copy, so only the folders the player writes to are kept.
    private static readonly string[] PLAYER_FOLDERS = ["Saves", "Workspaces"];
    private const int BACKUPS_KEPT = 5;
    private const string FILE_PREFIX = "saves-";

    public string? BackupDirectory => ResolveGameDataFolder(null) is { } gameData ? DirectoryFor(gameData) : null;

    public SaveBackup? BackUp(string? installDir = null, CancellationToken cancellationToken = default) =>
        ResolveGameDataFolder(installDir) is { } gameData
            ? BackUpFolder(gameData, skipWhenUnchanged: true, prune: true, cancellationToken)
            : null;

    // On Linux each Proton prefix has its own saves, so every game data folder gets its own backups. Otherwise a
    // newer backup of one folder would make another look unchanged, and a restore could cross between them.
    private string DirectoryFor(string gameData)
    {
        var normalized = fileSystem.Path.GetFullPath(gameData).TrimEnd('/', '\\').ToLowerInvariant();
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12];
        return fileSystem.Path.Combine(launcherConfigService.GetLocalStorageDirectory(), "save-backups", key);
    }

    private SaveBackup? BackUpFolder(string gameData, bool skipWhenUnchanged, bool prune, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(gameData);
        var files = PLAYER_FOLDERS
            .Select(folder => fileSystem.Path.Combine(gameData, folder))
            .Where(fileSystem.Directory.Exists)
            .SelectMany(folder => fileSystem.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .ToList();
        if (files.Count == 0) return null;

        var newestChange = files.Max(file => fileSystem.File.GetLastWriteTimeUtc(file));
        if (skipWhenUnchanged && ListIn(directory).FirstOrDefault() is { } newest && newestChange < fileSystem.File.GetLastWriteTimeUtc(newest.Path)) return null;

        fileSystem.Directory.CreateDirectory(directory);
        var path = fileSystem.Path.Combine(directory, $"{FILE_PREFIX}{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.zip");
        var partial = path + ".partial";
        try
        {
            using (var stream = fileSystem.File.Create(partial))
            using (var zip = zipFileService.NewArchive(stream, ZipArchiveMode.Create, false))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = fileSystem.Path.GetRelativePath(gameData, file).Replace('\\', '/');
                    using var source = fileSystem.FileStream.New(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = zip.CreateEntry(entryName).Open();
                    source.CopyTo(target);
                }
            }

            fileSystem.File.Move(partial, path);
        }
        catch
        {
            try { fileSystem.File.Delete(partial); } catch { }
            throw;
        }

        if (prune)
        {
            foreach (var old in ListIn(directory).Skip(BACKUPS_KEPT))
            {
                // A zip the player has open in another program stays until the next backup.
                try { fileSystem.File.Delete(old.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        return ToBackup(path);
    }

    public IReadOnlyList<SaveBackup> List() => ResolveGameDataFolder(null) is { } gameData ? ListIn(DirectoryFor(gameData)) : [];

    private List<SaveBackup> ListIn(string directory)
    {
        if (!fileSystem.Directory.Exists(directory)) return [];
        return fileSystem.Directory.EnumerateFiles(directory, FILE_PREFIX + "*.zip")
            .Select(ToBackup)
            .OrderByDescending(backup => backup.Created)
            .ToList();
    }

    public void Restore(SaveBackup backup)
    {
        var gameData = ResolveGameDataFolder(null)
                       ?? throw new InvalidOperationException("The game data folder for the active install is unknown.");
        if (!string.Equals(fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(backup.Path)), fileSystem.Path.GetFullPath(DirectoryFor(gameData)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The backup belongs to the saves of another install.");
        }

        fileSystem.Directory.CreateDirectory(gameData);
        var staging = fileSystem.Path.Combine(gameData, $".redux-restore-{Guid.NewGuid():N}");
        try
        {
            Extract(backup.Path, staging);

            // Never skipped: a save copied in from elsewhere keeps its old timestamp and would look unchanged.
            // Not pruned either, so restoring cannot delete the oldest backup before the player has tried this one.
            BackUpFolder(gameData, skipWhenUnchanged: false, prune: false, CancellationToken.None);

            foreach (var folder in PLAYER_FOLDERS)
            {
                var restored = fileSystem.Path.Combine(staging, folder);
                if (!fileSystem.Directory.Exists(restored)) continue;

                var current = fileSystem.Path.Combine(gameData, folder);
                var replaced = fileSystem.Path.Combine(staging, folder + ".replaced");
                if (fileSystem.Directory.Exists(current)) fileSystem.Directory.Move(current, replaced);
                try
                {
                    fileSystem.Directory.Move(restored, current);
                }
                catch
                {
                    if (fileSystem.Directory.Exists(replaced)) fileSystem.Directory.Move(replaced, current);
                    throw;
                }
            }
        }
        finally
        {
            try { fileSystem.Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private void Extract(string zipPath, string staging)
    {
        var root = fileSystem.Path.GetFullPath(staging) + fileSystem.Path.DirectorySeparatorChar;
        try
        {
            using var stream = fileSystem.File.OpenRead(zipPath);
            using var zip = zipFileService.NewArchive(stream, ZipArchiveMode.Read, false);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var target = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(staging, entry.FullName));
                var topFolder = entry.FullName.Split('/', '\\')[0];
                if (!target.StartsWith(root, StringComparison.Ordinal) || !PLAYER_FOLDERS.Contains(topFolder))
                {
                    throw new InvalidDataException($"The backup holds a file outside the save folders: {entry.FullName}");
                }

                fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                using var output = fileSystem.File.Create(target);
                source.CopyTo(output);
            }
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"The backup {fileSystem.Path.GetFileName(zipPath)} could not be read: {e.Message}", e);
        }
    }

    private string? ResolveGameDataFolder(string? installDir)
    {
        var entry = installDir is null
            ? ksp2InstallService.ActiveEntry
            : ksp2InstallService.Entries.FirstOrDefault(candidate => IsInstallOf(candidate, installDir)) ?? ksp2InstallService.ActiveEntry;
        return gameDataFolderService.Resolve(entry);
    }

    private bool IsInstallOf(Ksp2InstallEntry entry, string installDir) =>
        !string.IsNullOrWhiteSpace(entry.ExePath) &&
        string.Equals(
            fileSystem.Path.GetFullPath(fileSystem.Path.GetDirectoryName(entry.ExePath)!).TrimEnd('/', '\\'),
            fileSystem.Path.GetFullPath(installDir).TrimEnd('/', '\\'),
            StringComparison.OrdinalIgnoreCase);

    private SaveBackup ToBackup(string path)
    {
        var info = fileSystem.FileInfo.New(path);
        return new SaveBackup(path, info.LastWriteTime, info.Length);
    }
}
