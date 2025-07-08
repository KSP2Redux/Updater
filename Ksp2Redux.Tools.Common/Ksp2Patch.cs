using BsDiff;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ksp2Redux.Tools.Common;

public class Ksp2Patch : IDisposable
{
    private readonly PatchManifest _manifest = new();
    private readonly ZipArchive _archive;
    private const string ManifestJsonFileName = "manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true
    };

    public Ksp2Patch(ZipArchive archive)
    {
        this._archive = archive;
        if (archive.Mode == ZipArchiveMode.Read)
        {
            ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestJsonFileName)!;
            using Stream stream = manifestEntry.Open();
            _manifest = JsonSerializer.Deserialize<PatchManifest>(stream, SerializerOptions)!;
        }
    }

    public static Ksp2Patch Empty(Stream? saveStream = null, bool leaveOpen = false)
    {
        saveStream ??= new MemoryStream();
        return new Ksp2Patch(new ZipArchive(saveStream, ZipArchiveMode.Create, leaveOpen));
    }

    public static FileStream FromDiff(string saveFile, string ksp2Directory, string targetDirectory)
    {
        if (!Directory.Exists(ksp2Directory))
        {
            throw new DirectoryNotFoundException(ksp2Directory);
        }

        if (!Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException(targetDirectory);
        }

        FileStream writeFile = File.Open(saveFile, FileMode.Create, FileAccess.Write);
        using (Ksp2Patch patch = Empty(writeFile, true))
        {
            ulong size = patch.RecursiveDiff(patch, ksp2Directory, targetDirectory);

            Console.WriteLine($"Expected result size: {size}");

            ZipArchiveEntry manifestEntry = patch._archive.CreateEntry(ManifestJsonFileName);
            using Stream entryStream = manifestEntry.Open();

            JsonSerializer.Serialize(entryStream, patch._manifest, SerializerOptions);
        }

        Console.WriteLine($"Stream size: {writeFile.Length}");

        return writeFile;
    }

    private ulong RecursiveDiff(Ksp2Patch patch, string originalDirectory, string patchDirectory, string prefix = "")
    {
        ulong sum = 0UL;
        var patchDir = new DirectoryInfo(patchDirectory);
        foreach (FileInfo file in patchDir.GetFiles())
        {
            if (FileInformation.IgnoreFiles.Contains(Path.Combine(prefix, file.Name))) continue;
            if (File.Exists(Path.Combine(originalDirectory, file.Name)))
            {
                Console.WriteLine($"Checking {prefix}/{file.Name}");
                byte[] oldBytes = File.ReadAllBytes(Path.Combine(originalDirectory, file.Name));
                byte[] newBytes = File.ReadAllBytes(file.FullName);
                if (oldBytes.SequenceEqual(newBytes))
                {
                    continue;
                }

                Console.WriteLine("Different, patching");
                using Stream diff = patch.CreateDiff(Path.Combine(prefix, file.Name));
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
                    fileName = Path.Combine(prefix, file.Name),
                    originalHash = oldSHA.Hash!,
                    finalHash = newSHA.Hash!,
                });
            }
            else
            {
                Console.WriteLine($"Copying {prefix}/{file.Name}");
                using Stream copy = patch.CreateCopy(Path.Combine(prefix, file.Name));
                using FileStream input = file.OpenRead();
                sum += (ulong)input.Length;
                input.CopyTo(copy);

                input.Position = 0;
                using var newSHA = SHA256.Create();
                newSHA.ComputeHash(input);
                _manifest.operations.Add(new PatchOperation
                {
                    action = PatchOperation.PatchAction.Add,
                    fileName = Path.Combine(prefix, file.Name),
                    finalHash = newSHA.Hash!,
                });
            }
        }

        foreach (DirectoryInfo dir in patchDir.GetDirectories())
        {
            string newDir = Path.Combine(originalDirectory, dir.Name);
            if (FileInformation.IgnoreDirectories.Contains(Path.Combine(prefix, dir.Name))) continue;
            sum += RecursiveDiff(patch, newDir, dir.FullName, Path.Combine(prefix, dir.Name));
        }

        return sum;
    }

    public static Ksp2Patch FromFile(string path)
    {
        return new Ksp2Patch(ZipFile.OpenRead(path));
    }

    public static async Task CopyFileAsync(string sourceFile, string destinationFile)
    {
        await using var sourceStream = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        await using var destinationStream = new FileStream(
            destinationFile,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        await sourceStream.CopyToAsync(destinationStream);
    }

    public static async Task AsyncCopyKsp2Directory(
        string ksp2Directory,
        string targetDirectory,
        Action<string>? log = null
    )
    {
        if (!Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        ksp2Directory = ksp2Directory.TrimEnd('\\', '/') + '\\';

        foreach (string file in FileInformation.CopyFiles.Where(file => File.Exists($"{ksp2Directory}{file}")))
        {
            log?.Invoke($"Copying {ksp2Directory}{file} to {targetDirectory}{file}");
            await CopyFileAsync($"{ksp2Directory}{file}", $"{targetDirectory}{file}");
        }

        foreach (string directory in FileInformation.CopyFolders.Where(directory =>
                     Directory.Exists($"{ksp2Directory}{directory}")
                 ))
        {
            log?.Invoke($"Copying directory {ksp2Directory}{directory} to {targetDirectory}{directory}");
            await AsyncCopyDirectory(
                $"{ksp2Directory}{directory}",
                $"{targetDirectory}{directory}",
                true,
                log
            );
        }
    }

    static async Task AsyncCopyDirectory(
        string sourceDir,
        string destinationDir,
        bool recursive,
        Action<string>? log = null
    )
    {
        // Get information about the source directory
        var dir = new DirectoryInfo(sourceDir);

        // Check if the source directory exists
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        // Cache directories before we start copying
        DirectoryInfo[] dirs = dir.GetDirectories();

        // Create the destination directory
        if (!Directory.Exists(destinationDir))
            Directory.CreateDirectory(destinationDir);

        FileInfo[] files = dir.GetFiles();
        bool logFiles = true;
        int countForProgress = 0;
        int maxCountForProgress = 0;
        int progress = 0;
        if (files.Length > 255)
        {
            log?.Invoke($"Copying {files.Length} files from {sourceDir} to {destinationDir}");
            logFiles = false;
            countForProgress = files.Length / 10;
            maxCountForProgress = files.Length / 10;
        }

        foreach (FileInfo file in files)
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            if (logFiles)
            {
                log?.Invoke($"Copying file {Path.Combine(sourceDir, file.Name)} to {targetFilePath}");
            }
            else
            {
                countForProgress--;
                if (countForProgress == 0)
                {
                    progress += 10;
                    log?.Invoke($"{progress}% complete");
                    countForProgress = maxCountForProgress;
                }
            }

            await CopyFileAsync(file.FullName, targetFilePath);
        }

        if (recursive)
        {
            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                log?.Invoke($"Copying directory {Path.Combine(sourceDir, subDir.Name)} to {newDestinationDir}");
                await AsyncCopyDirectory(subDir.FullName, newDestinationDir, true, log);
            }
        }
    }

    public async Task AsyncCopyAndApply(
        string ksp2Directory,
        string targetDirectory,
        Action<string>? log = null,
        Action<string>? error = null
    )
    {
        await AsyncCopyKsp2Directory(ksp2Directory, targetDirectory, log);
        await AsyncApply(targetDirectory, ksp2Directory, log, error);
    }

    public async Task AsyncApply(
        string targetDirectory,
        string? sourceDirectory = null,
        Action<string>? log = null,
        Action<string>? error = null
    )
    {
        sourceDirectory ??= targetDirectory;
        if (!Directory.Exists(targetDirectory))
        {
            throw new DirectoryNotFoundException(targetDirectory);
        }
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        sourceDirectory = sourceDirectory.TrimEnd('\\', '/') + '\\';
        bool checkForOld = sourceDirectory == targetDirectory;

        // 1. Extract patch entries to temp folder
        string tempPatchDir = Path.Combine(Path.GetTempPath(), "Ksp2ReduxPatch_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPatchDir);

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
                            string outPath = Path.Combine(tempPatchDir, operation.fileName + ".bsdiff");
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                            entry.ExtractToFile(outPath);
                        }

                        break;
                    }
                    case PatchOperation.PatchAction.Add:
                    {
                        ZipArchiveEntry? entry = _archive.GetEntry(operation.fileName);
                        if (entry != null)
                        {
                            string outPath = Path.Combine(tempPatchDir, operation.fileName);
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                            entry.ExtractToFile(outPath);
                        }

                        break;
                    }
                }
            }

            // 2. Patch in parallel
            int maxConcurrency = Math.Max(Environment.ProcessorCount, 2);
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
                            DirectoryInfo? parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                            Directory.CreateDirectory(parent!.FullName);
                            if (checkForOld)
                            {
                                if (!File.Exists($"{targetDirectory}{trueName}.unpatched"))
                                {
                                    if (!File.Exists($"{targetDirectory}{trueName}"))
                                    {
                                        throw new Exception(
                                            $"Failed to apply patch because {trueName} does not exist in target " +
                                            $"installation directory"
                                        );
                                    }

                                    log?.Invoke($"Creating unpatched original file for {trueName}");
                                    await CopyFileAsync($"{targetDirectory}{trueName}",
                                        $"{targetDirectory}{trueName}.unpatched");
                                }
                            }
                            else
                            {
                                if (!File.Exists($"{sourceDirectory}{trueName}"))
                                {
                                    throw new Exception(
                                        $"Failed to apply patch because {trueName} does not exist in target " +
                                        $"installation directory"
                                    );
                                }

                                log?.Invoke($"Creating unpatched original file for {trueName}");
                                await CopyFileAsync(
                                    $"{sourceDirectory}{trueName}",
                                    $"{targetDirectory}{trueName}.unpatched"
                                );
                            }

                            await using FileStream originalFile = File.OpenRead(
                                $"{targetDirectory}{trueName}.unpatched"
                            );
                            await using FileStream targetFile = File.Open(
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

                            log?.Invoke($"Applying binary patch to {trueName}");

                            try
                            {
                                string patchPath = Path.Combine(tempPatchDir, trueName + ".bsdiff");
                                BinaryPatch.Apply(originalFile, () =>
                                {
                                    var memory = new MemoryStream(File.ReadAllBytes(patchPath));
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
                        else if (operation.action == PatchOperation.PatchAction.Remove)
                        {
                            log?.Invoke($"Deleting {trueName}");

                            if (File.Exists($"{targetDirectory}{trueName}"))
                            {
                                File.Delete($"{targetDirectory}{trueName}");
                            }
                        }
                        else // Add
                        {
                            log?.Invoke($"Copying {trueName} from patch");

                            DirectoryInfo? parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                            Directory.CreateDirectory(parent!.FullName);

                            await using FileStream targetFile = File.Open(
                                $"{targetDirectory}{trueName}",
                                FileMode.Create,
                                FileAccess.ReadWrite
                            );
                            string patchPath = Path.Combine(tempPatchDir, trueName);
                            await using FileStream entryStream = File.OpenRead(patchPath);
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
                Directory.Delete(tempPatchDir, true);
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
}