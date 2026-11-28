using System.IO.Abstractions;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Keeps an installation's boot.config at the settings of its release and of the launcher, outside every install transaction.
/// </summary>
public interface IBootConfigSettingsService
{
    /// <summary>
    /// Bring boot.config to the release's settings plus the launcher's own, which win.
    /// </summary>
    /// <remarks>
    /// The release's keys overwrite the player's own edits of those same keys on every apply. That is intended:
    /// the file follows the release, and uninstall puts the stock state back.
    /// The first time a key is managed, its stock state is recorded on the installation as its original: the
    /// storefront value for the three settings every stock file has, otherwise that the key is absent. The current
    /// file content is not the original, because an earlier launcher build may have written to it. The record is
    /// saved before the file is written, and a record that cannot be saved stops the write for this time.
    /// A key that is no longer wanted goes back to its original, and leaves the record only after that write worked.
    /// build-guid is never managed. Nothing is written when the file is already correct.
    /// Content never causes a failure. A file that cannot be read or written, or is larger than
    /// <see cref="BootConfigFile.MAX_BYTES"/>, is logged and tried again next time. The file is replaced as a whole,
    /// from a temporary file in its folder, so an interrupted write leaves the old content, and the temporary files
    /// an earlier interrupted write left there are deleted first.
    /// </remarks>
    /// <param name="entry">The configured installation.</param>
    void Apply(Ksp2InstallEntry entry);

    /// <summary>
    /// Record the boot.config settings of the release now installed.
    /// </summary>
    /// <param name="entry">The configured installation.</param>
    /// <param name="settings">The keys and values of the release, or null for none.</param>
    void SetReleaseSettings(Ksp2InstallEntry entry, IReadOnlyDictionary<string, string>? settings);

    /// <summary>
    /// Restore the archived original settings together with the install profile's boot.config undo record.
    /// </summary>
    /// <remarks>
    /// For an installation an earlier launcher converted, whose stock archive still holds the original file and
    /// which never recorded the keys that launcher wrote. Archived values take precedence over the undo record.
    /// A readable legacy archive also establishes that Redux boot keys it does not list were absent in stock.
    /// </remarks>
    /// <param name="entry">The configured installation.</param>
    /// <param name="stock">The keys and values of the original boot.config.</param>
    /// <returns>True if the file and its saved undo record no longer need recovery.</returns>
    bool TryRestoreStockSettings(Ksp2InstallEntry entry, IReadOnlyDictionary<string, string> stock);

    /// <summary>
    /// Put every recorded key back to its stock state, delete the keys a stock file lacks, and clear the record.
    /// </summary>
    /// <remarks>
    /// Without a record the file stays as it is. When the file cannot be restored the record stays, and a later call
    /// tries again. The launcher makes that call at every start for an installation that is no longer Redux.
    /// </remarks>
    /// <param name="entry">The configured installation.</param>
    void Revert(Ksp2InstallEntry entry);

    /// <summary>
    /// Clear saved boot settings after a legacy snapshot restores the entire stock file.
    /// </summary>
    void ForgetRestoredStockSettings(Ksp2InstallEntry entry);
}

/// <inheritdoc />
public class BootConfigSettingsService(ILauncherConfigService configuration, IFileSystem fileSystem, ILogService log)
    : IBootConfigSettingsService
{
    private const string GRAPHICS_JOBS_KEY = "gfx-enable-gfx-jobs";
    private static readonly string[] LEGACY_REDUX_KEYS = [GRAPHICS_JOBS_KEY, "gfx-threading-mode", "enable-directstorage"];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Apply(Ksp2InstallEntry entry)
    {
        lock (_gate)
        {
            if (PathOf(entry) is not { } path)
                return;
            byte[] content;
            try
            {
                if (!fileSystem.File.Exists(path))
                {
                    log.Info($"There is no boot.config at {path}. Its settings were skipped.");
                    return;
                }
                if (Read(path) is not { } read)
                    return;
                content = read;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not read {path}: {failure.Message} Its settings will be tried again later.");
                return;
            }

            Dictionary<string, string> desired = Desired(entry);
            var originals = new Dictionary<string, string?>(entry.BootConfigOriginals, StringComparer.Ordinal);
            foreach (string key in desired.Keys)
                originals.TryAdd(key, BootConfigFile.StockValue(key));

            var changes = new Dictionary<string, string?>(desired.Count, StringComparer.Ordinal);
            var dropped = new List<string>();
            foreach (var (key, original) in originals)
            {
                // build-guid and unusable keys are never edited, so a record of one only ever leaves the record.
                if (!BootConfigFile.IsValidSetting(key, ""))
                {
                    dropped.Add(key);
                    continue;
                }
                if (desired.TryGetValue(key, out string? value))
                {
                    changes[key] = value;
                }
                else
                {
                    changes[key] = original;
                    dropped.Add(key);
                }
            }
            byte[] edited = BootConfigFile.Edit(content, changes);
            if (!FitsWhenWritten(path, edited))
                return;
            // The record comes first, so a write that happens never lacks the original it replaces.
            if (originals.Count != entry.BootConfigOriginals.Count && !Persist(entry, originals))
            {
                log.Warn($"Could not save the boot.config undo record. {path} was left as it is and will be tried again later.");
                return;
            }
            if (!ReferenceEquals(edited, content) && !Write(path, edited))
                return;
            // Only a key that went back to its original, or already was there, leaves the record.
            if (dropped.Count == 0)
                return;
            foreach (string key in dropped)
                originals.Remove(key);
            entry.BootConfigOriginals = originals;
            configuration.Save();
        }
    }

    /// <inheritdoc />
    public void SetReleaseSettings(Ksp2InstallEntry entry, IReadOnlyDictionary<string, string>? settings)
    {
        lock (_gate)
        {
            Dictionary<string, string>? wanted = settings is { Count: > 0 } ? new(settings, StringComparer.Ordinal) : null;
            Dictionary<string, string>? current = entry.BootConfigReleaseSettings;
            if (wanted is null ? current is null : current is not null && current.Count == wanted.Count && !current.Except(wanted).Any())
                return;
            entry.BootConfigReleaseSettings = wanted;
            configuration.Save();
        }
    }

    /// <inheritdoc />
    public bool TryRestoreStockSettings(Ksp2InstallEntry entry, IReadOnlyDictionary<string, string> stock) => Revert(entry, stock);

    /// <inheritdoc />
    public void Revert(Ksp2InstallEntry entry) => Revert(entry, null);

    /// <inheritdoc />
    public void ForgetRestoredStockSettings(Ksp2InstallEntry entry)
    {
        lock (_gate)
        {
            if (entry.BootConfigOriginals.Count == 0 && entry.BootConfigReleaseSettings is null)
                return;

            entry.BootConfigOriginals = [];
            entry.BootConfigReleaseSettings = null;
            configuration.Save();
        }
    }

    private bool Revert(Ksp2InstallEntry entry, IReadOnlyDictionary<string, string>? stock)
    {
        lock (_gate)
        {
            var originals = new Dictionary<string, string?>(entry.BootConfigOriginals, StringComparer.Ordinal);
            if (stock is not null)
            {
                foreach (var (key, value) in stock)
                {
                    if (BootConfigFile.IsValidSetting(key, value))
                        originals[key] = value;
                }
                // Older launchers copied the whole Unity boot file without keeping per-key undo records.
                // The catalog keeps the archived settings until this succeeds, including after a restart.
                if (stock.Count != 0)
                {
                    foreach (string key in LEGACY_REDUX_KEYS)
                        originals.TryAdd(key, null);
                }
            }
            if (originals.Count != 0 && PathOf(entry) is { } path)
            {
                try
                {
                    if (!fileSystem.File.Exists(path))
                    {
                        log.Info($"There is no boot.config at {path}. Its recorded settings were dropped.");
                    }
                    else
                    {
                        byte[]? content = Read(path);
                        byte[]? edited = content is null ? null : BootConfigFile.Edit(content, originals
                            .Where(original => BootConfigFile.IsValidSetting(original.Key, ""))
                            .ToDictionary(original => original.Key, original => original.Value));
                        // Keep the record if the file cannot be read or the edit would make it unreadable.
                        if (edited is null || !FitsWhenWritten(path, edited))
                        {
                            if (stock is null)
                                SetReleaseSettings(entry, null);
                            return false;
                        }
                        if (!ReferenceEquals(edited, content))
                            Replace(path, edited);
                    }
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // The record stays, so the next start can try again.
                    log.Warn($"Could not restore {path}: {failure.Message} Its settings will be restored later.");
                    if (stock is null)
                        SetReleaseSettings(entry, null);
                    return false;
                }
            }
            if (entry.BootConfigOriginals.Count == 0 && entry.BootConfigReleaseSettings is null)
                return true;
            var previousOriginals = entry.BootConfigOriginals;
            var previousRelease = entry.BootConfigReleaseSettings;
            entry.BootConfigOriginals = [];
            entry.BootConfigReleaseSettings = null;
            if (stock is null)
            {
                configuration.Save();
                return true;
            }
            if (configuration.TrySave())
                return true;
            entry.BootConfigOriginals = previousOriginals;
            entry.BootConfigReleaseSettings = previousRelease;
            log.Warn("Could not clear the saved boot.config undo record. Stock settings will be restored again later.");
            return false;
        }
    }

    // The release's settings first, so the launcher's own settings win.
    private static Dictionary<string, string> Desired(Ksp2InstallEntry entry)
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entry.BootConfigReleaseSettings ?? [])
        {
            if (BootConfigFile.IsValidSetting(key, value))
                desired[key] = value;
        }
        desired[GRAPHICS_JOBS_KEY] = entry.DisableGraphicsJobs ? "0" : "1";
        return desired;
    }

    // Replaces the record on the installation, or leaves it as it was when it cannot be saved.
    private bool Persist(Ksp2InstallEntry entry, Dictionary<string, string?> originals)
    {
        Dictionary<string, string?> previous = entry.BootConfigOriginals;
        entry.BootConfigOriginals = originals;
        if (configuration.TrySave())
            return true;
        entry.BootConfigOriginals = previous;
        return false;
    }

    // The file's bytes, or null when it is larger than the launcher reads.
    private byte[]? Read(string path)
    {
        if (fileSystem.FileInfo.New(path).Length > BootConfigFile.MAX_BYTES)
        {
            log.Warn($"{path} is larger than {BootConfigFile.MAX_BYTES / 1024} KiB. Its settings were skipped.");
            return null;
        }
        byte[] content = fileSystem.File.ReadAllBytes(path);
        if (content.Length <= BootConfigFile.MAX_BYTES)
            return content;
        log.Warn($"{path} is larger than {BootConfigFile.MAX_BYTES / 1024} KiB. Its settings were skipped.");
        return null;
    }

    private bool FitsWhenWritten(string path, byte[] content)
    {
        if (content.Length <= BootConfigFile.MAX_BYTES)
            return true;
        log.Warn($"Editing {path} would make it larger than {BootConfigFile.MAX_BYTES / 1024} KiB. Its settings were skipped.");
        return false;
    }

    // Writes a temporary file beside the target, flushes it to disk and moves it over, so no reader or crash sees a
    // partial file, and a power loss after the move cannot leave a file that was never written.
    private void Replace(string path, byte[] content)
    {
        string folder = fileSystem.Path.GetDirectoryName(path)!;
        DeleteLeftovers(folder);
        string temporary = fileSystem.Path.Combine(folder, $"boot.config.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileSystemStream stream = fileSystem.FileStream.New(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            fileSystem.File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                fileSystem.File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The stray temporary file is harmless.
            }
            throw;
        }
    }

    // Temporary files of earlier runs that stopped between the write and the move. Only this service's own name,
    // boot.config. and 32 hex digits and .tmp, in the boot.config folder itself, is touched.
    private void DeleteLeftovers(string folder)
    {
        try
        {
            foreach (string file in fileSystem.Directory.EnumerateFiles(folder, "boot.config.*.tmp"))
            {
                string name = fileSystem.Path.GetFileName(file);
                string middle = name["boot.config.".Length..^".tmp".Length];
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigit))
                    continue;
                try
                {
                    fileSystem.File.Delete(file);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Could not delete the leftover {file}: {failure.Message}");
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not look for leftover temporary files in {folder}: {failure.Message}");
        }
    }

    private bool Write(string path, byte[] content)
    {
        try
        {
            Replace(path, content);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not write {path}: {failure.Message} Its settings will be tried again later.");
            return false;
        }
    }

    private string? PathOf(Ksp2InstallEntry entry) =>
        fileSystem.Path.GetDirectoryName(entry.ExePath) is { Length: > 0 } root
            ? fileSystem.Path.Combine(root, "KSP2_x64_Data", "boot.config")
            : null;
}
