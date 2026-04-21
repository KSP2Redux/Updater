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
    private readonly ZipArchive _archive;
    private const string ManifestJsonFileName = "manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

    public Ksp2Patch(IFileSystem fileSystem, ZipArchive archive)
    {
        _fileSystem = fileSystem;
        this._archive = archive;
        if (archive.Mode == ZipArchiveMode.Read)
        {
            ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestJsonFileName)!;
            using Stream stream = manifestEntry.Open();
            _manifest = JsonSerializer.Deserialize<PatchManifest>(stream, SerializerOptions)!;
        }
    }

    public static Ksp2Patch Empty(IFileSystem fileSystem, Stream? saveStream = null, bool leaveOpen = false)
    {
        saveStream ??= new MemoryStream();
        return new Ksp2Patch(fileSystem, new ZipArchive(saveStream, ZipArchiveMode.Create, leaveOpen));
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

            ZipArchiveEntry manifestEntry = patch._archive.CreateEntry(ManifestJsonFileName);
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
            if (FileInformation.IgnoreFiles.Contains(_fileSystem.Path.Combine(prefix, file.Name))) continue;
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

                _manifest.operations.Add(new PatchOperation
                {
                    action = PatchOperation.PatchAction.Patch,
                    fileName = _fileSystem.Path.Combine(prefix, file.Name),
                    originalHash = oldSHA.Hash!,
                    finalHash = newSHA.Hash!,
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
                _manifest.operations.Add(new PatchOperation
                {
                    action = PatchOperation.PatchAction.Add,
                    fileName = _fileSystem.Path.Combine(prefix, file.Name),
                    finalHash = newSHA.Hash!,
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
                    _manifest.operations.Add(new PatchOperation
                    {
                        fileName = _fileSystem.Path.Combine(prefix, file.Name),
                        action = PatchOperation.PatchAction.Remove
                    });
                }
            }

            foreach (var dir in originalDir.GetDirectories())
            {
                if (!_fileSystem.Directory.Exists(_fileSystem.Path.Combine(patchDirectory, dir.Name)))
                {
                    _manifest.operations.Add(new PatchOperation
                    {
                        fileName = _fileSystem.Path.Combine(prefix, dir.Name),
                        action = PatchOperation.PatchAction.Remove
                    });
                }
            }
        }

        foreach (IDirectoryInfo dir in patchDir.GetDirectories())
        {
            string newDir = _fileSystem.Path.Combine(originalDirectory, dir.Name);
            if (FileInformation.IgnoreDirectories.Contains(_fileSystem.Path.Combine(prefix, dir.Name))) continue;
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
        await using var source = new FileStream(
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
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        ksp2Directory = ksp2Directory.TrimEnd('\\', '/') + '\\';

        foreach (string file in FileInformation.CopyFiles.Where(file => fileSystem.File.Exists($"{ksp2Directory}{file}")))
        {
            await CopyFileAsync(fileSystem, $"{ksp2Directory}{file}", $"{targetDirectory}{file}");
        }

        foreach (string directory in FileInformation.CopyFolders.Where(directory =>
                     fileSystem.Directory.Exists($"{ksp2Directory}{directory}")
                 ))
        {
            log?.Invoke($"Copying directory {ksp2Directory}{directory} to {targetDirectory}{directory}");
            await AsyncCopyDirectory(
                fileSystem,
                $"{ksp2Directory}{directory}",
                $"{targetDirectory}{directory}",
                true,
                log
            );
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
        var src = new DirectoryInfo(sourceDir);
        if (!src.Exists) throw new DirectoryNotFoundException(sourceDir);

        Directory.CreateDirectory(destinationDir);
        
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

        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        sourceDirectory = sourceDirectory.TrimEnd('\\', '/') + '\\';

        // 1. Extract patch entries to temp folder
        string tempPatchDir = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "Ksp2ReduxPatch_" + Guid.NewGuid());
        _fileSystem.Directory.CreateDirectory(tempPatchDir);

        try
        {
            // Extract patch & add entries
            foreach (PatchOperation operation in _manifest.operations)
            {
                switch (operation.action)
                {
                    case PatchOperation.PatchAction.Patch:
                    {
                        ZipArchiveEntry? entry = _archive.GetEntry(operation.fileName + ".bsdiff");
                        if (entry != null)
                        {
                            string outPath = _fileSystem.Path.Combine(tempPatchDir, operation.fileName + ".bsdiff");
                            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(outPath)!);
                            entry.ExtractToFile(outPath);
                        }

                        break;
                    }
                    case PatchOperation.PatchAction.Add:
                    {
                        ZipArchiveEntry? entry = _archive.GetEntry(operation.fileName);
                        if (entry != null)
                        {
                            string outPath = _fileSystem.Path.Combine(tempPatchDir, operation.fileName);
                            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(outPath)!);
                            entry.ExtractToFile(outPath);
                        }

                        break;
                    }
                }
            }

            // 2. Patch in parallel
            int maxConcurrency = Math.Max(environmentProvider.ProcessorCount, 2);
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task>();

            foreach (PatchOperation operation in _manifest.operations)
            {
                await semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        string trueName = operation.fileName;
                        if (operation.action == PatchOperation.PatchAction.Patch)
                        {
                            log?.Invoke($"Applying binary patch to {trueName}");
                            IDirectoryInfo? parent = _fileSystem.FileInfo.New($"{targetDirectory}{trueName}").Directory;
                            _fileSystem.Directory.CreateDirectory(parent!.FullName);
                            if (!_fileSystem.File.Exists($"{sourceDirectory}{trueName}"))
                            {
                                throw new Exception(
                                    $"Failed to apply patch because {trueName} does not exist in target " +
                                    $"installation directory"
                                );
                            }
                            log?.Invoke($"Creating temporary file for {trueName}");
                            await CopyFileAsync(
                                _fileSystem,
                                $"{sourceDirectory}{trueName}",
                                $"{targetDirectory}{trueName}.temp"
                            );

                            await using (FileSystemStream originalFile = _fileSystem.File.OpenRead($"{targetDirectory}{trueName}.temp"))
                            {
                                await using FileSystemStream targetFile = _fileSystem.File.Open(
                                    $"{targetDirectory}{trueName}",
                                    FileMode.Create,
                                    FileAccess.ReadWrite
                                );

                                if (!await ValidateFileHashAsync(originalFile, operation.originalHash!))
                                {
                                    throw new InvalidDataException(
                                        $"File {originalFile.Name} does not match expected hash " +
                                        $"{FormatHash(operation.originalHash!)}. Cannot apply patch! Check that the " +
                                        $"Redux patch you are applying is for the version of the game (Steam, portable " +
                                        $"zip, or Epic) you are patching."
                                    );
                                }


                                try
                                {
                                    string patchPath = _fileSystem.Path.Combine(tempPatchDir, trueName + ".bsdiff");
                                    BinaryPatch.Apply(originalFile, () =>
                                    {
                                        var memory = new MemoryStream(_fileSystem.File.ReadAllBytes(patchPath));
                                        return memory;
                                    }, targetFile);
                                }
                                catch (Exception e)
                                {
                                    error?.Invoke(e.Message);
                                    throw;
                                }

                                if (!await ValidateFileHashAsync(targetFile, operation.finalHash!))
                                {
                                    throw new InvalidDataException(
                                        $"File {targetFile.Name} does not match expected hash " +
                                        $"{FormatHash(operation.finalHash!)}."
                                    );
                                }
                            }
                            
                            // Delete the original file if we are not caching it
                            _fileSystem.File.Delete($"{targetDirectory}{trueName}.temp");
                        }
                        else if (operation.action == PatchOperation.PatchAction.Remove)
                        {
                            log?.Invoke($"Deleting {trueName}");

                            if (_fileSystem.Directory.Exists($"{targetDirectory}{trueName}"))
                            {
                                _fileSystem.Directory.Delete($"{targetDirectory}{trueName}", true);
                            }
                            else if (_fileSystem.File.Exists($"{targetDirectory}{trueName}"))
                            {
                                _fileSystem.File.Delete($"{targetDirectory}{trueName}");
                            }
                        }
                        else // Add
                        {
                            
                            log?.Invoke($"Copying {trueName} from patch");

                            IDirectoryInfo? parent = _fileSystem.FileInfo.New($"{targetDirectory}{trueName}").Directory;
                            _fileSystem.Directory.CreateDirectory(parent!.FullName);

                            await using FileSystemStream targetFile = _fileSystem.File.Open(
                                $"{targetDirectory}{trueName}",
                                FileMode.Create,
                                FileAccess.ReadWrite
                            );
                            string patchPath = _fileSystem.Path.Combine(tempPatchDir, trueName);
                            await using FileSystemStream entryStream = _fileSystem.File.OpenRead(patchPath);
                            await entryStream.CopyToAsync(targetFile);

                            if (!await ValidateFileHashAsync(targetFile, operation.finalHash!))
                            {
                                throw new InvalidDataException(
                                    $"File {targetFile.Name} does not match expected hash " +
                                    $"{FormatHash(operation.finalHash!)}."
                                );
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
        ZipArchiveEntry path = _archive.CreateEntry(diffPath + ".bsdiff");
        return path.Open();
    }

    public Stream CreateCopy(string copyPath)
    {
        ZipArchiveEntry path = _archive.CreateEntry(copyPath);
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
        => $"Ksp2Patch: Operations:{_manifest.operations.Count}";
}