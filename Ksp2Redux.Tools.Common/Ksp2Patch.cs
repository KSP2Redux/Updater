using System.IO.Compression;
using System.Text;
using BsDiff;

namespace Ksp2Redux.Tools.Common;

public class Ksp2Patch(ZipArchive archive) : IDisposable
{
    public static Ksp2Patch Empty(Stream? saveStream=null, bool leaveOpen=false)
    {
        saveStream ??= new MemoryStream();
        return new Ksp2Patch(new ZipArchive(saveStream, ZipArchiveMode.Create, leaveOpen));
    }

    public static FileStream FromDiff(string saveFile, string ksp2Directory, string targetDirectory)
    {
        if (!Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException(targetDirectory);
        var writeFile = File.Open(saveFile, FileMode.Create, FileAccess.Write);
        using (var patch = Empty(writeFile,true))
        {
            var size = RecursiveDiff(patch, ksp2Directory, targetDirectory);

            Console.WriteLine($"Expected result size: {size}");
        }
        Console.WriteLine($"Stream size: {writeFile.Length}");
        return writeFile;
    }

    private static ulong RecursiveDiff(Ksp2Patch patch, string originalDirectory, string patchDirectory, string prefix = "")
    {
        var sum = 0UL;
        var patchDir = new DirectoryInfo(patchDirectory);
        foreach (var file in patchDir.GetFiles())
        {
            if (FileInformation.IgnoreFiles.Contains(Path.Combine(prefix,file.Name))) continue;
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
            }
            else
            {
                Console.WriteLine($"Copying {prefix}/{file.Name}");
                using var copy = patch.CreateCopy(Path.Combine(prefix, file.Name));
                using var input = file.OpenRead();
                sum += (ulong)input.Length;
                input.CopyTo(copy);
                
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

    public static void CopyKsp2Directory(string ksp2Directory, string targetDirectory)
    {
        if (!Directory.Exists(ksp2Directory)) throw new DirectoryNotFoundException(ksp2Directory);
        if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        ksp2Directory = ksp2Directory.TrimEnd('\\', '/') + '\\';
        foreach (var file in FileInformation.CopyFiles.Where(file => File.Exists($"{ksp2Directory}{file}")))
        {
            File.Copy($"{ksp2Directory}{file}", $"{targetDirectory}{file}", true);
        }

        foreach (var directory in FileInformation.CopyFolders)
        {
            Console.WriteLine($"{ksp2Directory}{directory} - {Directory.Exists($"{ksp2Directory}{directory}")}");
        }
        
        foreach (var directory in FileInformation.CopyFolders.Where(directory =>
                     Directory.Exists($"{ksp2Directory}{directory}")))
        {
            
            CopyDirectory($"{ksp2Directory}{directory}", $"{targetDirectory}{directory}", true);
        }
    }
    static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
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
            file.CopyTo(targetFilePath, true);
        }

        // If recursive and copying subdirectories, recursively call this method
        if (!recursive) return;
        foreach (DirectoryInfo subDir in dirs)
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir, true);
        }
    }

    public void CopyAndApply(string ksp2Directory, string targetDirectory)
    {
        CopyKsp2Directory(ksp2Directory, targetDirectory);
        Apply(targetDirectory, ksp2Directory);
    }
    
    public void Apply(string targetDirectory, string? sourceDirectory=null)
    {
        sourceDirectory ??= targetDirectory;
        if (!Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException(targetDirectory);
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        targetDirectory = targetDirectory.TrimEnd('\\', '/') + '\\';
        sourceDirectory = sourceDirectory.TrimEnd('\\', '/') + '\\';
        bool checkForOld = sourceDirectory == targetDirectory;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".bsdiff"))
            {
                var trueName = entry.FullName[..^7];
                var parent = new FileInfo($"{targetDirectory}{trueName}").Directory;
                Directory.CreateDirectory(parent!.FullName);
                if (checkForOld)
                {
                    if (!File.Exists($"{targetDirectory}{trueName}.unpatched"))
                    {
                        if (!File.Exists($"{targetDirectory}{trueName}"))
                            throw new Exception(
                                $"Failed to apply patch because {trueName} does not exist in target installation directory");
                        File.Copy($"{targetDirectory}{trueName}", $"{targetDirectory}{trueName}.unpatched");
                    }
                    
                }
                else
                {
                    
                    if (!File.Exists($"{sourceDirectory}{trueName}"))
                        throw new Exception(
                            $"Failed to apply patch because {trueName} does not exist in target installation directory");
                    File.Copy($"{sourceDirectory}{trueName}", $"{targetDirectory}{trueName}.unpatched", true);
                }

                using var originalFile = File.OpenRead($"{targetDirectory}{trueName}.unpatched");
                using var targetFile = File.Open($"{targetDirectory}{trueName}", FileMode.Create, FileAccess.Write);
                BinaryPatch.Apply(originalFile, () =>
                {
                    using var nonMemory = entry.Open();
                    var memory = new MemoryStream();
                    nonMemory.CopyTo(memory);
                    memory.Seek(0, SeekOrigin.Begin);
                    return memory;
                }, targetFile);
            } else if (entry.FullName.EndsWith(".remove"))
            {
                var trueName = entry.FullName[..^7];
                if (File.Exists($"{targetDirectory}{trueName}")) File.Delete($"{targetDirectory}{trueName}");
            }
            else
            {
                var parent = new FileInfo($"{targetDirectory}{entry.FullName}").Directory;
                Directory.CreateDirectory(parent!.FullName);
                using var targetFile = File.Open($"{targetDirectory}{entry.FullName}", FileMode.Create, FileAccess.Write);
                using var entryStream = entry.Open();
                entryStream.CopyTo(targetFile);
            }
        }
    }

    public Stream CreateDiff(string diffPath)
    {
        var path = archive.CreateEntry(diffPath+".bsdiff");
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
        var sb = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.EndsWith(".bsdiff"))
            {
                sb.AppendLine($"MOD {entry.FullName[0..^7]}");
            } else if (entry.Name.EndsWith(".remove"))
            {
                sb.AppendLine($"DEL {entry.FullName[0..^7]}");
            }
            else
            {
                sb.AppendLine($"COP {entry.FullName}");
            }
        }
        return sb.ToString();
    }
    
    public void Dispose()
    {
        archive.Dispose();
    }
}