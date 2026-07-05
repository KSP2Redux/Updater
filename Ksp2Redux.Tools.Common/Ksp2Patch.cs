using System.IO.Abstractions;
using BsDiff;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Service;

namespace Ksp2Redux.Tools.Common;

public class Ksp2Patch : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly PatchManifest _manifest = new();
    private readonly IZipArchive _archive;
    private const string ManifestJsonFileName = "manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public Ksp2Patch(IFileSystem fileSystem, IZipArchive archive)
    {
        _fileSystem = fileSystem;
        this._archive = archive;
        if (archive.Mode == ZipArchiveMode.Read)
        {
            IZipArchiveEntry manifestEntry = archive.GetEntry(ManifestJsonFileName)!;
            using Stream stream = manifestEntry.Open();
            _manifest = JsonSerializer.Deserialize<PatchManifest>(stream, SerializerOptions)!;
        }
    }

    public static Ksp2Patch Empty(IFileSystem fileSystem, Stream? saveStream = null, bool leaveOpen = false)
    {
        saveStream ??= new MemoryStream();
        // Only allowed to disable here because it's not used by the launcher
#pragma warning disable RS0030
        return new Ksp2Patch(fileSystem, new ZipArchiveWrapper(new ZipArchive(saveStream, ZipArchiveMode.Create, leaveOpen)));
#pragma warning restore RS0030
    }

    public static FileSystemStream FromDiff(IFileSystem fileSystem, string saveFile, string ksp2Directory, string targetDirectory, bool checkRemovals=false)
    {
        if (!fileSystem.Directory.Exists(ksp2Directory))
        {
            throw new DirectoryNotFoundException(ksp2Directory);
        }

        if (!fileSystem.Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException(targetDirectory);
        }

        FileSystemStream writeFile = fileSystem.File.Open(saveFile, FileMode.Create, FileAccess.Write);
        using (Ksp2Patch patch = Empty(fileSystem, writeFile, true))
        {
            ulong size = patch.RecursiveDiff(patch, ksp2Directory, targetDirectory, checkRemovals);

            Console.WriteLine($"Expected result size: {size}");

            IZipArchiveEntry manifestEntry = patch._archive.CreateEntry(ManifestJsonFileName);
            using Stream entryStream = manifestEntry.Open();

            JsonSerializer.Serialize(entryStream, patch._manifest, SerializerOptions);
        }

        Console.WriteLine($"Stream size: {writeFile.Length}");

        return writeFile;
    }

    private ulong RecursiveDiff(Ksp2Patch patch, string originalDirectory, string patchDirectory, bool checkRemovals, string prefix = "")
    {
        ulong sum = 0UL;
        var patchDir = _fileSystem.DirectoryInfo.New(patchDirectory);
        foreach (IFileInfo file in patchDir.GetFiles())
        {
            if (FileInformation.IgnoreFiles(_fileSystem).Contains(_fileSystem.Path.Combine(prefix, file.Name))) continue;
            if (_fileSystem.File.Exists(_fileSystem.Path.Combine(originalDirectory, file.Name)))
            {
                Console.WriteLine($"Checking {prefix}/{file.Name}");
                byte[] oldBytes = _fileSystem.File.ReadAllBytes(_fileSystem.Path.Combine(originalDirectory, file.Name));
                byte[] newBytes = _fileSystem.File.ReadAllBytes(file.FullName);
                if (oldBytes.SequenceEqual(newBytes))
                {
                    continue;
                }

                Console.WriteLine("Different, patching");
                using Stream diff = patch.CreateDiff(_fileSystem.Path.Combine(prefix, file.Name));
                using var diffMem = new MemoryStream();
                BinaryPatch.Create(oldBytes, newBytes, diffMem);
                sum += (ulong)diffMem.Length;
                diffMem.Seek(0, SeekOrigin.Begin);
                diffMem.CopyTo(diff);

                using var oldSHA = SHA256.Create();
                using var newSHA = SHA256.Create();

                oldSHA.ComputeHash(oldBytes);
                newSHA.ComputeHash(newBytes);
                Console.WriteLine($"Original SHA256: {FormatHash(oldSHA.Hash!)}");
                Console.WriteLine($"New SHA256: {FormatHash(newSHA.Hash!)}");

                _manifest.Operations.Add(new PatchOperation
                {
                    Action = PatchOperation.PatchAction.Patch,
                    FileName = _fileSystem.Path.Combine(prefix, file.Name),
                    OriginalHash = oldSHA.Hash!,
                    FinalHash = newSHA.Hash!,
                });
            }
            else
            {
                Console.WriteLine($"Copying {prefix}/{file.Name}");
                using Stream copy = patch.CreateCopy(_fileSystem.Path.Combine(prefix, file.Name));
                using FileSystemStream input = file.OpenRead();
                sum += (ulong)input.Length;
                input.CopyTo(copy);

                input.Position = 0;
                using var newSHA = SHA256.Create();
                newSHA.ComputeHash(input);
                _manifest.Operations.Add(new PatchOperation
                {
                    Action = PatchOperation.PatchAction.Add,
                    FileName = _fileSystem.Path.Combine(prefix, file.Name),
                    FinalHash = newSHA.Hash!,
                });
            }
        }


        if (checkRemovals)
        {
            var originalDir = _fileSystem.DirectoryInfo.New(originalDirectory);
            foreach (IFileInfo file in originalDir.GetFiles())
            {
                if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(patchDirectory, file.Name)))
                {
                    _manifest.Operations.Add(new PatchOperation
                    {
                        FileName = _fileSystem.Path.Combine(prefix, file.Name),
                        Action = PatchOperation.PatchAction.Remove
                    });
                }
            }

            foreach (var dir in originalDir.GetDirectories())
            {
                if (!_fileSystem.Directory.Exists(_fileSystem.Path.Combine(patchDirectory, dir.Name)))
                {
                    _manifest.Operations.Add(new PatchOperation
                    {
                        FileName = _fileSystem.Path.Combine(prefix, dir.Name),
                        Action = PatchOperation.PatchAction.Remove
                    });
                }
            }
        }

        foreach (IDirectoryInfo dir in patchDir.GetDirectories())
        {
            string newDir = _fileSystem.Path.Combine(originalDirectory, dir.Name);
            if (FileInformation.IgnoreDirectories(_fileSystem).Contains(_fileSystem.Path.Combine(prefix, dir.Name))) continue;
            sum += RecursiveDiff(patch, newDir, dir.FullName, checkRemovals, _fileSystem.Path.Combine(prefix, dir.Name));
        }

        return sum;
    }

    public static Ksp2Patch FromFile(IFileSystem fileSystem, IZipFileService zipFileService, string path)
    {
        return new Ksp2Patch(fileSystem, zipFileService.OpenRead(path));
    }

    public static async Task CopyFileAsync(IFileSystem fileSystem, string sourceFile, string destinationFile)
    {
        const int bufferSize = 1024 * 1024; // 1 MiB
        await using var source = fileSystem.FileStream.New(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var dest = fileSystem.FileStream.New(
            destinationFile, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);

        try { dest.SetLength(source.Length); } catch { /* not critical */ }

        await source.CopyToAsync(dest, bufferSize);
    }

    public static async Task AsyncCopyKsp2Directory(
        IFileSystem fileSystem,
        string ksp2Directory,
        string targetDirectory,
        Action<string>? log = null
    )
    {
        if (!fileSystem.Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!fileSystem.Directory.Exists(targetDirectory)) fileSystem.Directory.CreateDirectory(targetDirectory);

        foreach (string file in FileInformation.CopyFiles.Where(file =>
                     fileSystem.File.Exists(fileSystem.Path.Combine(ksp2Directory, file))))
        {
            await CopyFileAsync(fileSystem,
                fileSystem.Path.Combine(ksp2Directory, file),
                fileSystem.Path.Combine(targetDirectory, file));
        }

        foreach (string directory in FileInformation.CopyFolders.Where(directory =>
                     fileSystem.Directory.Exists(fileSystem.Path.Combine(ksp2Directory, directory))))
        {
            var source = fileSystem.Path.Combine(ksp2Directory, directory);
            var destination = fileSystem.Path.Combine(targetDirectory, directory);
            log?.Invoke($"Copying directory {source} to {destination}");
            await AsyncCopyDirectory(fileSystem, source, destination, true, log);
        }
    }

    private static async Task AsyncCopyDirectory(
        IFileSystem fileSystem,
        string sourceDir,
        string destinationDir,
        bool recursive,
        Action<string>? log = null
    )
    {
        var src = fileSystem.DirectoryInfo.New(sourceDir);
        if (!src.Exists) throw new DirectoryNotFoundException(sourceDir);

        fileSystem.Directory.CreateDirectory(destinationDir);
        
        foreach (var d in src.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            fileSystem.Directory.CreateDirectory(fileSystem.Path.Join(destinationDir, fileSystem.Path.GetRelativePath(sourceDir, d.FullName)));
        }

        var semaphore = new SemaphoreSlim(8);
        var tasks = new List<Task>();

        foreach (var file in src.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = fileSystem.Path.GetRelativePath(sourceDir, file.FullName);
            var dst = fileSystem.Path.Join(destinationDir, rel);

            await semaphore.WaitAsync().ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await CopyFileAsync(fileSystem, file.FullName, dst).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task AsyncCopyAndApply(
        IEnvironmentProvider environmentProvider,
        string ksp2Directory,
        string targetDirectory,
        Action<string>? log = null,
        Action<string>? error = null
    )
    {
        await AsyncCopyKsp2Directory(_fileSystem, ksp2Directory, targetDirectory, log);
        await AsyncApply(environmentProvider, targetDirectory, ksp2Directory, log, error);
    }

    public async Task AsyncApply(
        IEnvironmentProvider environmentProvider,
        string targetDirectory,
        string? sourceDirectory = null,
        Action<string>? log = null,
        Action<string>? error = null
    )
    {
        sourceDirectory ??= targetDirectory;
        if (!_fileSystem.Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException(targetDirectory);
        }
        if (!_fileSystem.Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        // 1. Extract patch entries to temp folder
        string tempPatchDir = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "Ksp2ReduxPatch_" + Guid.NewGuid());
        _fileSystem.Directory.CreateDirectory(tempPatchDir);

        try
        {
            // Extract patch & add entries
            foreach (PatchOperation operation in _manifest.Operations)
            {
                if (FileInformation.IgnoreFiles(_fileSystem).Contains(NormalizeEntryPath(operation.FileName))) continue;
                string entryFsName = NormalizeEntryPath(operation.FileName);
                switch (operation.Action)
                {
                    case PatchOperation.PatchAction.Patch:
                    {
                        IZipArchiveEntry? entry = _archive.GetEntry(operation.FileName + ".bsdiff");
                        if (entry != null)
                        {
                            string outPath = ResolveContainedPath(tempPatchDir, entryFsName + ".bsdiff");
                            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(outPath)!);
                            entry.ExtractToFile(_fileSystem, outPath);
                        }

                        break;
                    }
                    case PatchOperation.PatchAction.Add:
                    {
                        IZipArchiveEntry? entry = _archive.GetEntry(operation.FileName);
                        if (entry != null)
                        {
                            string outPath = ResolveContainedPath(tempPatchDir, entryFsName);
                            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(outPath)!);
                            entry.ExtractToFile(_fileSystem, outPath);
                        }

                        break;
                    }
                }
            }

            // 2. Patch in parallel
            int maxConcurrency = Math.Max(environmentProvider.ProcessorCount, 2);
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task>();

            foreach (PatchOperation operation in _manifest.Operations)
            {
                if (FileInformation.IgnoreFiles(_fileSystem).Contains(NormalizeEntryPath(operation.FileName))) continue;
                await semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        string trueName = NormalizeEntryPath(operation.FileName);
                        string targetPath = ResolveContainedPath(targetDirectory, trueName);
                        string sourcePath = ResolveContainedPath(sourceDirectory, trueName);
                        string tempPath = targetPath + ".temp";

                        // Each file write below is already atomic (verify-then-swap), so if a previous
                        // attempt at this same plan got partway through before failing elsewhere, this
                        // file may already be sitting in its correct final state. Retrying the whole
                        // plan shouldn't mean redownloading/reapplying files that are already done.
                        if (operation.Action is PatchOperation.PatchAction.Patch or PatchOperation.PatchAction.Add &&
                            operation.FinalHash is not null && _fileSystem.File.Exists(targetPath))
                        {
                            await using FileSystemStream existingFile = _fileSystem.File.OpenRead(targetPath);
                            if (await ValidateFileHashAsync(existingFile, operation.FinalHash))
                            {
                                log?.Invoke($"{trueName} already matches the expected result, skipping.");
                                return;
                            }
                        }

                        if (operation.Action == PatchOperation.PatchAction.Patch)
                        {
                            log?.Invoke($"Applying binary patch to {trueName}");
                            IDirectoryInfo? parent = _fileSystem.FileInfo.New(targetPath).Directory;
                            _fileSystem.Directory.CreateDirectory(parent!.FullName);
                            if (!_fileSystem.File.Exists(sourcePath))
                            {
                                throw new Exception(
                                    $"Failed to apply patch because {trueName} does not exist in target " +
                                    $"installation directory"
                                );
                            }
                            log?.Invoke($"Creating temporary file for {trueName}");
                            await RetryOnFileLockAsync(
                                () => CopyFileAsync(_fileSystem, sourcePath, tempPath), trueName, log);

                            // Write the patched result to a co-located swap file and verify it fully
                            // before ever touching the real target file, instead of truncating the
                            // target up front - a crash or disk-full mid-write must never leave the
                            // live game file half-written.
                            string swapPath = targetPath + ".newtemp";
                            try
                            {
                                await using (FileSystemStream originalFile = _fileSystem.File.OpenRead(tempPath))
                                {
                                    if (!await ValidateFileHashAsync(originalFile, operation.OriginalHash!))
                                    {
                                        throw new InvalidDataException(
                                            $"File {originalFile.Name} does not match expected hash " +
                                            $"{FormatHash(operation.OriginalHash!)}. Cannot apply patch! Check that the " +
                                            $"Redux patch you are applying is for the version of the game (Steam, portable " +
                                            $"zip, or Epic) you are patching."
                                        );
                                    }

                                    await using (FileSystemStream swapFile = _fileSystem.File.Open(
                                                     swapPath, FileMode.Create, FileAccess.ReadWrite))
                                    {
                                        try
                                        {
                                            string patchPath = _fileSystem.Path.Combine(tempPatchDir, trueName + ".bsdiff");
                                            BinaryPatch.Apply(originalFile, () =>
                                            {
                                                var memory = new MemoryStream(_fileSystem.File.ReadAllBytes(patchPath));
                                                return memory;
                                            }, swapFile);
                                        }
                                        catch (Exception e)
                                        {
                                            error?.Invoke(e.Message);
                                            throw;
                                        }

                                        if (!await ValidateFileHashAsync(swapFile, operation.FinalHash!))
                                        {
                                            throw new InvalidDataException(
                                                $"Patched result for {trueName} does not match expected hash " +
                                                $"{FormatHash(operation.FinalHash!)}."
                                            );
                                        }
                                    }
                                }

                                // Only now, with a fully verified replacement sitting beside it, do we
                                // touch the real file - a single atomic rename instead of an in-place write.
                                await RetryOnFileLockAsync(
                                    () => { _fileSystem.File.Move(swapPath, targetPath, true); return Task.CompletedTask; },
                                    trueName, log);
                            }
                            finally
                            {
                                try { if (_fileSystem.File.Exists(swapPath)) _fileSystem.File.Delete(swapPath); }
                                catch { /* best-effort cleanup */ }
                            }

                            // Delete the original file if we are not caching it
                            _fileSystem.File.Delete(tempPath);
                        }
                        else if (operation.Action == PatchOperation.PatchAction.Remove)
                        {
                            log?.Invoke($"Deleting {trueName}");

                            if (_fileSystem.Directory.Exists(targetPath))
                            {
                                _fileSystem.Directory.Delete(targetPath, true);
                            }
                            else if (_fileSystem.File.Exists(targetPath))
                            {
                                _fileSystem.File.Delete(targetPath);
                            }
                        }
                        else // Add
                        {
                            log?.Invoke($"Copying {trueName} from patch");

                            IDirectoryInfo? parent = _fileSystem.FileInfo.New(targetPath).Directory;
                            _fileSystem.Directory.CreateDirectory(parent!.FullName);

                            // Stage and verify beside the target, then atomically swap it in - the
                            // extracted file already sits complete in tempPatchDir, but that may be on
                            // a different volume, so copy it in next to the target first to make the
                            // final swap a same-volume rename rather than a cross-volume copy.
                            string swapPath = targetPath + ".newtemp";
                            try
                            {
                                string patchPath = _fileSystem.Path.Combine(tempPatchDir, trueName);
                                await using (FileSystemStream swapFile = _fileSystem.File.Open(
                                                 swapPath, FileMode.Create, FileAccess.ReadWrite))
                                {
                                    await using FileSystemStream entryStream = _fileSystem.File.OpenRead(patchPath);
                                    await entryStream.CopyToAsync(swapFile);

                                    if (!await ValidateFileHashAsync(swapFile, operation.FinalHash!))
                                    {
                                        throw new InvalidDataException(
                                            $"File {trueName} does not match expected hash " +
                                            $"{FormatHash(operation.FinalHash!)}."
                                        );
                                    }
                                }

                                await RetryOnFileLockAsync(
                                    () => { _fileSystem.File.Move(swapPath, targetPath, true); return Task.CompletedTask; },
                                    trueName, log);
                            }
                            finally
                            {
                                try { if (_fileSystem.File.Exists(swapPath)) _fileSystem.File.Delete(swapPath); }
                                catch { /* best-effort cleanup */ }
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            try
            {
                _fileSystem.Directory.Delete(tempPatchDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    public Stream CreateDiff(string diffPath)
    {
        IZipArchiveEntry path = _archive.CreateEntry(diffPath + ".bsdiff");
        return path.Open();
    }

    public Stream CreateCopy(string copyPath)
    {
        IZipArchiveEntry path = _archive.CreateEntry(copyPath);
        return path.Open();
    }

    public void CreateRemove(string removePath)
    {
        _archive.CreateEntry(removePath + ".remove");
    }

    public string GetDiffInfo()
    {
        using var reader = new StreamReader(_archive.GetEntry(ManifestJsonFileName)!.Open());

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Retries an operation a few times with a short, increasing delay when it fails with an
    /// IOException - antivirus mid-scan or the game itself still holding a handle on a file being
    /// patched is common and almost always resolves within a second or two on its own. Without this,
    /// a purely transient lock forces the same expensive whole-install rollback as a real failure.
    /// </summary>
    private static async Task RetryOnFileLockAsync(Func<Task> action, string fileDescription, Action<string>? log)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                log?.Invoke($"{fileDescription} appears to be in use (attempt {attempt}/{maxAttempts}), retrying shortly...");
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt));
            }
        }
    }

    private string NormalizeEntryPath(string path) =>
        path.Replace('\\', _fileSystem.Path.DirectorySeparatorChar).Replace('/', _fileSystem.Path.DirectorySeparatorChar);

    /// <summary>
    /// Combines <paramref name="baseDirectory"/> with a patch entry's (already-normalized) relative
    /// path and asserts the result actually stays under that directory. Only the archive's own
    /// whole-file checksum is verified upstream (ManifestReleasesFeed) - nothing otherwise stops a
    /// corrupted or tampered manifest.json from naming an entry like "../../../Windows/System32/x"
    /// and writing or deleting files outside the install directory entirely.
    /// </summary>
    private string ResolveContainedPath(string baseDirectory, string relativePath)
    {
        string fullBase = _fileSystem.Path.GetFullPath(baseDirectory);
        string fullCombined = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(fullBase, relativePath));

        bool isContained = fullCombined.Equals(fullBase, StringComparison.OrdinalIgnoreCase) ||
                            fullCombined.StartsWith(fullBase + _fileSystem.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!isContained)
        {
            throw new InvalidDataException(
                $"Patch entry '{relativePath}' resolves outside of '{fullBase}'. Refusing to apply - " +
                "this patch may be corrupted or tampered with.");
        }

        return fullCombined;
    }

    private static bool ValidateFileHash(Stream stream, byte[] hash)
    {
        var wasPosition = stream.Position;
        stream.Position = 0;
        using SHA256 sha = SHA256.Create();
        sha.ComputeHash(stream);
        stream.Position = wasPosition;
        return sha.Hash!.SequenceEqual(hash);
    }

    private static async Task<bool> ValidateFileHashAsync(Stream stream, byte[] hash)
    {
        long wasPosition = stream.Position;
        stream.Position = 0;
        using var sha = SHA256.Create();
        await sha.ComputeHashAsync(stream);
        stream.Position = wasPosition;
        return sha.Hash!.SequenceEqual(hash);
    }

    private static string FormatHash(byte[] hashBytes)
    {
        StringBuilder hashString = new(64);
        foreach (byte x in hashBytes)
        {
            hashString.Append($"{x:x2}");
        }

        return hashString.ToString();
    }

    public void Dispose()
    {
        _archive.Dispose();
    }

    public override string ToString()
        => $"Ksp2Patch: Operations:{_manifest.Operations.Count}";
}