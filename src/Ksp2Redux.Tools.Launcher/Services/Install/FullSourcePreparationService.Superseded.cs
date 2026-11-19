using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public partial class FullSourcePreparationService
{
    private const string TRASH_FOLDER = "trash";

    // Caches written before boot.config left every contract are named by the digest of a contract that listed it.
    // Such a twin that holds exactly the current contract's bytes is renamed to the current name instead of being built
    // again, which needs no free space. A twin whose bytes are verified elsewhere is renamed into the trash folder and
    // deleted from there. A rename is atomic, so an interrupted delete leaves only trash, and the next run finishes it.
    // Only direct entries of baselines, prepatch-backups and the trash folder are touched. Anything that fails a check
    // stays where it is.

    // Finish deletions that an earlier run started.
    internal void SweepTrash()
    {
        try
        {
            string trash = At(CacheRoot(), TRASH_FOLDER);
            if (!_fs.Directory.Exists(trash))
                return;
            foreach (string leftover in _fs.Directory.EnumerateFileSystemEntries(trash).ToArray())
                DeleteTrash(leftover);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Cleanup is optional. The next preparation tries again.
        }
    }

    private void DeleteTrash(string path)
    {
        try
        {
            _paths.ValidateControlledPath(path);
            if (!_fs.Directory.Exists(path))
            {
                _fs.File.Delete(path);
                return;
            }
            foreach (string child in _fs.Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                _paths.ValidateControlledPath(child);
            _fs.Directory.Delete(path, true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // What is left is still trash. The next preparation tries again.
        }
    }

    // Rename a superseded cache entry into the trash folder, then delete it there.
    private void Discard(string path, string folder)
    {
        RequireCacheChild(path, folder);
        string trash = At(CacheRoot(), TRASH_FOLDER);
        CreateDirectory(trash);
        string target = At(trash, Guid.NewGuid().ToString("N"));
        _paths.ValidateControlledPath(path);
        _paths.ValidateControlledPath(target);
        if (_fs.Directory.Exists(path))
            _fs.Directory.Move(path, target);
        else
            _fs.File.Move(path, target);
        DeleteTrash(target);
    }

    // The trees of the baselines folder that an earlier build kept for this contract.
    private List<string> SupersededBaselines(StockBaselineManifest contract)
    {
        var twins = new List<string>();
        if (!_os.IsWindows() || contract.HasUnixMetadata)
            return twins;
        string parent = At(CacheRoot(), "baselines");
        if (!_fs.Directory.Exists(parent))
            return twins;
        string current = BaselineRoot(contract);
        foreach (string candidate in _fs.Directory.EnumerateDirectories(parent).ToArray())
        {
            string name = _fs.Path.GetFileName(candidate);
            if (candidate == current || name.Length != 64 || !name.All(Uri.IsHexDigit))
                continue;
            // The name is a digest of the whole contract with the boot.config this tree holds, so it proves the match.
            string bootConfig = At(candidate, BootConfigFile.RELATIVE_PATH);
            try
            {
                _paths.ValidateControlledPath(bootConfig);
                if (!_fs.File.Exists(bootConfig) || _fs.FileInfo.New(bootConfig).Length > BootConfigFile.MAX_BYTES)
                    continue;
                byte[] bytes = _fs.File.ReadAllBytes(bootConfig);
                if (Digest(contract.ToJsonWithBootConfig(bytes.Length, Digest(bytes))).Equals(name, StringComparison.OrdinalIgnoreCase))
                    twins.Add(candidate);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A tree that cannot be inspected stays where it is.
            }
        }
        return twins;
    }

    // Move the twin of this contract into place when it holds the bytes that `inputs` expects. That tree needs no copy.
    private async Task<bool> AdoptSupersededBaselineAsync(StockBaselineManifest contract, StockBaselineManifest inputs,
        string root, CancellationToken ct)
    {
        try
        {
            if (!_os.IsWindows() || contract.HasUnixMetadata || _fs.Directory.Exists(root))
                return false;
            foreach (string twin in SupersededBaselines(contract))
            {
                try
                {
                    if (!await TreeMatchesAsync(twin, inputs, ct))
                        continue;
                    RequireCacheChild(twin, "baselines");
                    _paths.ValidateControlledPath(root);
                    _fs.Directory.Move(twin, root);
                    // The file is in no contract, and the player's own copy is no cache content.
                    string bootConfig = At(root, BootConfigFile.RELATIVE_PATH);
                    if (_fs.File.Exists(bootConfig))
                        _fs.File.Delete(bootConfig);
                    return true;
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // This twin is not usable. The next one or a rebuild follows.
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Adoption is optional. A rebuild follows.
        }
        return false;
    }

    // The caller verified the tree of this contract in its current form, so its twins are no longer needed.
    internal void PruneSupersededBaselines(StockBaselineManifest contract)
    {
        try
        {
            foreach (string twin in SupersededBaselines(contract))
            {
                try
                {
                    Discard(twin, "baselines");
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Cleanup is optional. The next verified preparation tries again.
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Cleanup is optional. The next verified preparation tries again.
        }
    }

    // The archives of the prepatch-backups folder that an earlier build kept for this contract. An earlier build's
    // archive carries the contract's older form as its embedded manifest, which its file name digests.
    private List<string> SupersededBackups(StockBaselineManifest contract, string currentBackup)
    {
        var twins = new List<string>();
        if (!_os.IsWindows() || contract.HasUnixMetadata)
            return twins;
        string parent = At(CacheRoot(), "prepatch-backups");
        if (!_fs.Directory.Exists(parent) || _fs.Path.GetDirectoryName(currentBackup) != parent)
            return twins;
        string currentDigest = Digest(contract.ToJson());
        foreach (string candidate in _fs.Directory.EnumerateFiles(parent, "*.zip").ToArray())
        {
            if (candidate == currentBackup)
                continue;
            if (ReadOlderContract(candidate) is { } older && older.Manifest.IsLegacyForm(older.Bytes) &&
                Digest(older.Manifest.ToJson()) == currentDigest &&
                _fs.Path.GetFileName(candidate) == Digest(older.Bytes).ToLowerInvariant() + ".zip")
                twins.Add(candidate);
        }
        return twins;
    }

    // Move the twin archive of this contract into place when it verifies. Verification already happened here.
    internal async Task<bool> AdoptSupersededBackupAsync(StockBaselineManifest contract, string backup, CancellationToken ct)
    {
        try
        {
            if (!_os.IsWindows() || contract.HasUnixMetadata || _fs.File.Exists(backup))
                return false;
            foreach (string twin in SupersededBackups(contract, backup))
            {
                try
                {
                    _paths.ValidateControlledPath(twin);
                    await _archive.VerifyAsync(twin, contract, ct);
                    RequireCacheChild(twin, "prepatch-backups");
                    _paths.ValidateControlledPath(backup);
                    _fs.File.Move(twin, backup);
                    return true;
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // This archive is not usable. The next one or a rebuild follows.
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Adoption is optional. A rebuild follows.
        }
        return false;
    }

    // The caller verified the backup of this contract in its current form.
    internal void PruneSupersededBackups(StockBaselineManifest contract, string verifiedBackup)
    {
        try
        {
            foreach (string twin in SupersededBackups(contract, verifiedBackup))
            {
                try
                {
                    Discard(twin, "prepatch-backups");
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Cleanup is optional. The next verified preparation tries again.
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Cleanup is optional. The next verified preparation tries again.
        }
    }

    private (byte[] Bytes, StockBaselineManifest Manifest)? ReadOlderContract(string archivePath)
    {
        try
        {
            byte[]? bytes = _archive.ReadEmbeddedManifest(archivePath);
            return bytes is null ? null : (bytes, StockBaselineManifest.FromJson(bytes));
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException or EndOfStreamException)
        {
            return null;
        }
    }
}
