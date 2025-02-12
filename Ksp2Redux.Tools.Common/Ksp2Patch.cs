using System.Collections;
using BsDiff;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ksp2Redux.Tools.Common;

public class Ksp2Patch : IDisposable
{
    private readonly PatchManifest manifest = new();
    private readonly ZipArchive archive;
    private const string manifestJsonFileName = "manifest.json";

    private static readonly JsonSerializerOptions serializerOptions = new() { WriteIndented = true, IncludeFields = true };

    public Ksp2Patch(ZipArchive archive)
    {
        this.archive = archive;
        if (archive.Mode == ZipArchiveMode.Read)
        {
            ZipArchiveEntry manifestEntry = archive.GetEntry(manifestJsonFileName)!;
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<PatchManifest>(stream, serializerOptions)!;
        }
    }

    public static Ksp2Patch Empty(Stream? saveStream = null, bool leaveOpen = false)
    {
        saveStream ??= new MemoryStream();
        return new Ksp2Patch(new ZipArchive(saveStream, ZipArchiveMode.Create, leaveOpen));
    }

    public static FileStream FromDiff(string saveFile, string ksp2Directory, string targetDirectory)
    {
        if (!Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException(targetDirectory);
        var writeFile = File.Open(saveFile, FileMode.Create, FileAccess.Write);

        using (var patch = Empty(writeFile, true))
        {
            var size = patch.RecursiveDiff(patch, ksp2Directory, targetDirectory);

            Console.WriteLine($"Expected result size: {size}");

            ZipArchiveEntry manifestEntry = patch.archive.CreateEntry(manifestJsonFileName);
            using var entryStream = manifestEntry.Open();

            JsonSerializer.Serialize(entryStream, patch.manifest, serializerOptions);
        }
        Console.WriteLine($"Stream size: {writeFile.Length}");

        return writeFile;
    }

    private ulong RecursiveDiff(Ksp2Patch patch, string originalDirectory, string patchDirectory, string prefix = "")
    {
        var sum = 0UL;
        var patchDir = new DirectoryInfo(patchDirectory);
        foreach (var file in patchDir.GetFiles())
        {
            if (FileInformation.IgnoreFiles.Contains(Path.Combine(prefix, file.Name))) continue;
            if (File.Exists(Path.Combine(originalDirectory, file.Name)))
            {
                Console.WriteLine($"Checking {prefix}/{file.Name}");
                var oldBytes = File.ReadAllBytes(Path.Combine(originalDirectory, file.Name));
                var newBytes = File.ReadAllBytes(file.FullName);
                if (oldBytes.SequenceEqual(newBytes)) continue;

                Console.WriteLine("Different, patching");
                using var diff = patch.CreateDiff(Path.Combine(prefix, file.Name));
                using var diffMem = new MemoryStream();
                BinaryPatch.Create(oldBytes, newBytes, diffMem);
                sum += (ulong)diffMem.Length;
                diffMem.Seek(0, SeekOrigin.Begin);
                diffMem.CopyTo(diff);

                using SHA256 oldSHA = SHA256.Create();
                using SHA256 newSHA = SHA256.Create();

                oldSHA.ComputeHash(oldBytes);
                newSHA.ComputeHash(newBytes);
                Console.WriteLine($"Original SHA256: {FormatHash(oldSHA.Hash!)}");
                Console.WriteLine($"New SHA256: {FormatHash(newSHA.Hash!)}");

                manifest.operations.Add(new()
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
                using var copy = patch.CreateCopy(Path.Combine(prefix, file.Name));
                using var input = file.OpenRead();
                sum += (ulong)input.Length;
                input.CopyTo(copy);

                input.Position = 0;
                using SHA256 newSHA = SHA256.Create();
                newSHA.ComputeHash(input);
                manifest.operations.Add(new()
                {
                    action = PatchOperation.PatchAction.Add,
                    fileName = Path.Combine(prefix, file.Name),
                    finalHash = newSHA.Hash!,
                });
            }
        }

        foreach (var dir in patchDir.GetDirectories())
        {
            var newDir = Path.Combine(originalDirectory, dir.Name);
            if (FileInformation.IgnoreDirectories.Contains(Path.Combine(prefix, dir.Name))) continue;
            sum += RecursiveDiff(patch, newDir, dir.FullName, Path.Combine(prefix, dir.Name));
        }

        return sum;
    }

    public static Ksp2Patch FromFile(string path)
    {
        return new Ksp2Patch(ZipFile.OpenRead(path));
    }

    public static void CopyKsp2Directory(string ksp2Directory, string targetDirectory, Action<string>? log = null)
    {
        if (!Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        ksp2Directory = ksp2Directory.TrimEnd('\\', '/') + '\\';
        foreach (var file in FileInformation.CopyFiles.Where(file => File.Exists($"{ksp2Directory}{file}")))
        {
            log?.Invoke($"Copying file {ksp2Directory}{file} to {targetDirectory}{file}");
            File.Copy($"{ksp2Directory}{file}", $"{targetDirectory}{file}", true);
        }

        foreach (var directory in FileInformation.CopyFolders)
        {
            Console.WriteLine($"{ksp2Directory}{directory} - {Directory.Exists($"{ksp2Directory}{directory}")}");
        }

        foreach (var directory in FileInformation.CopyFolders.Where(directory =>
                     Directory.Exists($"{ksp2Directory}{directory}")))
        {
            log?.Invoke($"Copying directory {ksp2Directory}{directory} to {targetDirectory}{directory}");
            CopyDirectory($"{ksp2Directory}{directory}", $"{targetDirectory}{directory}", true, log);
        }
    }

    
    public static async Task CopyFileAsync(string sourceFile, string destinationFile)
    {
        using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var destinationStream = new FileStream(destinationFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await sourceStream.CopyToAsync(destinationStream);
    }

    public static async Task AsyncCopyKsp2Directory(string ksp2Directory, string targetDirectory,
        Action<string>? log = null)
    {
        
        if (!Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        ksp2Directory = ksp2Directory.TrimEnd('\\', '/') + '\\';

        foreach (var file in FileInformation.CopyFiles.Where(file => File.Exists($"{ksp2Directory}{file}")))
        {
            log?.Invoke($"Copying {ksp2Directory}{file} to {targetDirectory}{file}");
            await CopyFileAsync($"{ksp2Directory}{file}", $"{targetDirectory}{file}");
        }
        
        foreach (var directory in FileInformation.CopyFolders.Where(directory =>
                     Directory.Exists($"{ksp2Directory}{directory}")))
        {
            log?.Invoke($"Copying directory {ksp2Directory}{directory} to {targetDirectory}{directory}");
            await AsyncCopyDirectory($"{ksp2Directory}{directory}", $"{targetDirectory}{directory}", true, log);
        }
    }
    
    static void CopyDirectory(string sourceDir, string destinationDir, bool recursive, Action<string>? log = null)
    {
        // Get information about the source directory
        var dir = new DirectoryInfo(sourceDir);

        // Check if the source directory exists
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        // Cache directories before we start copying
        DirectoryInfo[] dirs = dir.GetDirectories();

        // Create the destination directory
        if (!Directory.Exists(destinationDir))
            Directory.CreateDirectory(destinationDir);

        // Get the files in the source directory and copy to the destination directory
        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            log?.Invoke($"Copying file {Path.Combine(sourceDir,file.Name)} to {targetFilePath}");
            file.CopyTo(targetFilePath, true);
        }

        // If recursive and copying subdirectories, recursively call this method
        if (!recursive) return;
        foreach (DirectoryInfo subDir in dirs)
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            log?.Invoke($"Copying directory {Path.Combine(sourceDir,subDir.Name)} to {newDestinationDir}");
            CopyDirectory(subDir.FullName, newDestinationDir, true);
        }
    }

    static async Task AsyncCopyDirectory(string sourceDir, string destinationDir, bool recursive,
        Action<string>? log = null)
    {
        
        // Get information about the source directory
        var dir = new DirectoryInfo(sourceDir);

        // Check if the source directory exists
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        // Cache directories before we start copying
        DirectoryInfo[] dirs = dir.GetDirectories();

        // Create the destination directory
        if (!Directory.Exists(destinationDir))
            Directory.CreateDirectory(destinationDir);

        var files = dir.GetFiles();
        var logFiles = true;
        var countForProgress = 0;
        var maxCountForProgress = 0;
        var progress = 0;
        if (files.Length > 255)
        {
            log?.Invoke($"Copying {files.Length} files from {sourceDir} to {destinationDir}");
            logFiles = false;
            countForProgress = files.Length / 10;
            maxCountForProgress = files.Length / 10;
        }
        
        foreach (var file in files)
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
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
            // file.CopyTo(targetFilePath, true);
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

    public void CopyAndApply(string ksp2Directory, string targetDirectory, Action<string>? log = null)
    {
        CopyKsp2Directory(ksp2Directory, targetDirectory, log);
        Apply(targetDirectory, ksp2Directory, log);
    }

    public async Task AsyncCopyAndApply(string ksp2Directory, string targetDirectory, Action<string>? log = null, Action<string>? error = null)
    {
        await AsyncCopyKsp2Directory(ksp2Directory, targetDirectory, log);
        await AsyncApply(targetDirectory, ksp2Directory, log, error);
    }

    public void Apply(string targetDirectory, string? sourceDirectory = null, Action<string>? log = null)
    {
        sourceDirectory ??= targetDirectory;
        if (!Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException(targetDirectory);
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        sourceDirectory = sourceDirectory.TrimEnd('\\', '/') + '\\';
        bool checkForOld = sourceDirectory == targetDirectory;

        foreach (var operation in manifest.operations)
        {
            var trueName = operation.fileName;
            if (operation.action == PatchOperation.PatchAction.Patch)
            {
                var parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                Directory.CreateDirectory(parent!.FullName);
                if (checkForOld)
                {
                    if (!File.Exists($"{targetDirectory}{trueName}.unpatched"))
                    {
                        if (!File.Exists($"{targetDirectory}{trueName}"))
                            throw new Exception(
                                $"Failed to apply patch because {trueName} does not exist in target installation directory");
                        log?.Invoke($"Creating unpatched original file for {trueName}");
                        File.Copy($"{targetDirectory}{trueName}", $"{targetDirectory}{trueName}.unpatched");
                    }
                }
                else
                {
                    if (!File.Exists($"{sourceDirectory}{trueName}"))
                        throw new Exception(
                            $"Failed to apply patch because {trueName} does not exist in target installation directory");
                    log?.Invoke($"Creating unpatched original file for {trueName}");
                    File.Copy($"{sourceDirectory}{trueName}", $"{targetDirectory}{trueName}.unpatched", true);
                }

                using var originalFile = File.OpenRead($"{targetDirectory}{trueName}.unpatched");
                using var targetFile = File.Open($"{targetDirectory}{trueName}", FileMode.Create, FileAccess.ReadWrite);

                if (!ValidateFileHash(originalFile, operation.originalHash!))
                {
                    throw new InvalidDataException($"File {originalFile.Name} does not match expected hash {FormatHash(operation.originalHash!)}. "
                        + "Cannot apply patch! Check that the Redux patch you are applying is for the version of the game (Steam, portable zip, or Epic) you are patching.");
                }

                log?.Invoke($"Applying binary patch {originalFile} to {trueName}");
                BinaryPatch.Apply(originalFile, () =>
                {
                    using var nonMemory = archive.GetEntry(trueName + ".bsdiff")!.Open();
                    var memory = new MemoryStream();
                    nonMemory.CopyTo(memory);
                    memory.Seek(0, SeekOrigin.Begin);
                    return memory;
                }, targetFile);

                if (!ValidateFileHash(targetFile, operation.finalHash!))
                {
                    throw new InvalidDataException($"File {targetFile.Name} does not match expected hash {FormatHash(operation.finalHash!)}.");
                }
            }
            else if (operation.action == PatchOperation.PatchAction.Remove)
            {
                log?.Invoke($"Deleting {trueName}");
                if (File.Exists($"{targetDirectory}{trueName}")) File.Delete($"{targetDirectory}{trueName}");
            }
            else
            {
                log?.Invoke($"Copying {trueName} from patch");
                var parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                Directory.CreateDirectory(parent!.FullName);
                using var targetFile = File.Open($"{targetDirectory}{trueName}", FileMode.Create, FileAccess.ReadWrite);
                using var entryStream = archive.GetEntry(trueName)!.Open();
                entryStream.CopyTo(targetFile);

                if (!ValidateFileHash(targetFile, operation.finalHash!))
                {
                    throw new InvalidDataException($"File {targetFile.Name} does not match expected hash {FormatHash(operation.finalHash!)}.");
                }
            }
        }
    }

    public async Task AsyncApply(string targetDirectory, string? sourceDirectory = null, Action<string>? log = null, Action<string>? error = null)
    {
                sourceDirectory ??= targetDirectory;
        if (!Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException(targetDirectory);
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        sourceDirectory = sourceDirectory.TrimEnd('\\', '/') + '\\';
        bool checkForOld = sourceDirectory == targetDirectory;

        foreach (var operation in manifest.operations)
        {
            var trueName = operation.fileName;
            if (operation.action == PatchOperation.PatchAction.Patch)
            {
                var parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                Directory.CreateDirectory(parent!.FullName);
                if (checkForOld)
                {
                    if (!File.Exists($"{targetDirectory}{trueName}.unpatched"))
                    {
                        if (!File.Exists($"{targetDirectory}{trueName}"))
                        {
                            throw new Exception(
                                $"Failed to apply patch because {trueName} does not exist in target installation directory");
                        }
                        log?.Invoke($"Creating unpatched original file for {trueName}");
                        await CopyFileAsync($"{targetDirectory}{trueName}", $"{targetDirectory}{trueName}.unpatched");
                    }
                }
                else
                {
                    if (!File.Exists($"{sourceDirectory}{trueName}"))
                    {
                        throw new Exception(
                            $"Failed to apply patch because {trueName} does not exist in target installation directory");
                    }
                    log?.Invoke($"Creating unpatched original file for {trueName}");
                    await CopyFileAsync($"{sourceDirectory}{trueName}", $"{targetDirectory}{trueName}.unpatched");
                }

                await using var originalFile = File.OpenRead($"{targetDirectory}{trueName}.unpatched");
                await using var targetFile = File.Open($"{targetDirectory}{trueName}", FileMode.Create, FileAccess.ReadWrite);

                if (!await ValidateFileHashAsync(originalFile, operation.originalHash!))
                {
                    throw new InvalidDataException($"File {originalFile.Name} does not match expected hash {FormatHash(operation.originalHash!)}. "
                                                + "Cannot apply patch! Check that the Redux patch you are applying is for the version of the game (Steam, portable zip, or Epic) you are patching.");
                }

                log?.Invoke($"Applying binary patch to {trueName}");
                try
                {
                    BinaryPatch.Apply(originalFile, () =>
                    {
                        using var nonMemory = archive.GetEntry(trueName + ".bsdiff")!.Open();
                        var memory = new MemoryStream();
                        nonMemory.CopyTo(memory);
                        memory.Seek(0, SeekOrigin.Begin);
                        return memory;
                    }, targetFile);
                }
                catch (Exception e)
                {
                    error?.Invoke(e.Message);
                    break;
                }

                if (!await ValidateFileHashAsync(targetFile, operation.finalHash!))
                {
                    throw new InvalidDataException($"File {targetFile.Name} does not match expected hash {FormatHash(operation.finalHash!)}.");
                }
            }
            else if (operation.action == PatchOperation.PatchAction.Remove)
            {
                log?.Invoke($"Deleting {trueName}");
                if (File.Exists($"{targetDirectory}{trueName}")) File.Delete($"{targetDirectory}{trueName}");
            }
            else
            {
                log?.Invoke($"Copying {trueName} from patch");
                var parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                Directory.CreateDirectory(parent!.FullName);
                await using var targetFile = File.Open($"{targetDirectory}{trueName}", FileMode.Create, FileAccess.ReadWrite);
                await using var entryStream = archive.GetEntry(trueName)!.Open();
                await entryStream.CopyToAsync(targetFile);

                if (!await ValidateFileHashAsync(targetFile, operation.finalHash!))
                { 
                    throw new InvalidDataException($"File {targetFile.Name} does not match expected hash {FormatHash(operation.finalHash!)}.");
                }
            }
        }
    }

    public Stream CreateDiff(string diffPath)
    {
        var path = archive.CreateEntry(diffPath + ".bsdiff");
        return path.Open();
    }

    public Stream CreateCopy(string copyPath)
    {
        var path = archive.CreateEntry(copyPath);
        return path.Open();
    }

    public void CreateRemove(string removePath)
    {
        archive.CreateEntry(removePath + ".remove");
    }
    public string GetDiffInfo()
    {
        using var reader = new StreamReader(archive.GetEntry(manifestJsonFileName)!.Open());

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
        var wasPosition = stream.Position;
        stream.Position = 0;
        using SHA256 sha = SHA256.Create();
        await sha.ComputeHashAsync(stream);
        stream.Position = wasPosition;
        return sha.Hash!.SequenceEqual(hash);
    }

    private static string FormatHash(byte[] hashBytes)
    {
        StringBuilder hashString = new(64);
        foreach (byte x in hashBytes)
        {
            hashString.AppendFormat("{0:x2}", x);
        }
        return hashString.ToString();
    }

    public void Dispose()
    {
        archive.Dispose();
    }
}