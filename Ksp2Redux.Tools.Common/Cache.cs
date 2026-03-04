

using System.IO.Compression;

namespace Ksp2Redux.Tools.Common;

public static class Cache
{
    public static List<string> IgnoredDirectories = [Path.Combine("KSP2_x64_Data","StreamingAssets"), "UninstallTemp", "mods"];
    public static List<string> SavedDirectories = ["Redux/Config"];


    public static void RecursivelyCreateCache(string directory)
    {
        if (File.Exists(Path.Combine(directory, "uninstall.zip")))
        {
            File.Delete(Path.Combine(directory, "uninstall.zip"));
        }
        
        
        using var saveStream = File.Open(Path.Combine(directory, "uninstall.zip"), FileMode.Create, FileAccess.Write);
        using (var memoryStream = new MemoryStream())
        {
            using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                AddFolder(zipArchive, directory, "");
            }
            memoryStream.Seek(0, SeekOrigin.Begin);
            memoryStream.CopyTo(saveStream);
        }
    }

    public static void AddFolder(ZipArchive archive, string directory, string prefix)
    {
        if (IgnoredDirectories.Contains(prefix))
        {
            return;
        }
        var dir = new DirectoryInfo(directory);
        foreach (var file in dir.GetFiles())
        {
            if (file.Name == "uninstall.zip" && prefix == "") continue;
            archive.CreateEntryFromFile(file.FullName, Path.Combine(prefix, file.Name));
        }

        foreach (var subDir in dir.GetDirectories())
        {
            AddFolder(archive, Path.Combine(directory, subDir.Name), Path.Combine(prefix, subDir.Name));
        }
    }
    
    // If isForRepatch is true, then the Redux/Config folder is saved, and 
    public static void RecursivelyRestoreCache(string directory, bool isForRepatch=false)
    {
        if (!File.Exists(Path.Combine(directory, "uninstall.zip")))
        {
            throw new Exception("Original stock files were deleted, uninstallation is impossible");
        }

        if (isForRepatch)
        {
            Directory.CreateDirectory(Path.Combine(directory, "UninstallTemp"));
            foreach (var dir in SavedDirectories)
            {
                if (Directory.Exists(Path.Combine(directory, dir)))
                {
                    var parentDirectory = new DirectoryInfo(Path.Combine(directory, "UninstallTemp", dir)).Parent!;
                    if (!parentDirectory.Exists)
                    {
                        parentDirectory.Create();
                    }
                    Directory.Move(Path.Combine(directory, dir), Path.Combine(directory, "UninstallTemp", dir));
                }
            }
        }
        
        ClearOutFolder(directory);

        using (var zipFile = ZipFile.OpenRead(Path.Combine(directory, "uninstall.zip")))
        {
            zipFile.ExtractToDirectory(directory, true);
        }
        
        if (!isForRepatch)
        {
            File.Delete(Path.Combine(directory, "uninstall.zip"));
        }
        else
        {
            foreach (var dir in SavedDirectories)
            {
                if (Directory.Exists(Path.Combine(directory, "UninstallTemp", dir)))
                {
                    var parentDirectory = new DirectoryInfo(Path.Combine(directory, dir)).Parent!;
                    if (!parentDirectory.Exists)
                    {
                        parentDirectory.Create();
                    }
                    Directory.Move(Path.Combine(directory, "UninstallTemp", dir), Path.Combine(directory, dir));
                }
            }
            Directory.Delete(Path.Combine(directory, "UninstallTemp"), true);
        }
    }

    private static bool ClearOutFolder(string dir, string prefix="")
    {
        if (IgnoredDirectories.Contains(prefix))
        {
            return false;
        }

        var info = new DirectoryInfo(dir);
        foreach (var file in info.GetFiles())
        {
            if (file.Name == "uninstall.zip" && prefix == "") continue;
            file.Delete();
        }

        bool removeFolder = true;
        foreach (var directory in info.GetDirectories())
        {
            var prefix2 = Path.Combine(prefix, directory.Name);
            var dir2 = Path.Combine(dir, directory.Name);
            if (ClearOutFolder(dir2, prefix2))
            {
                directory.Delete(true);
            }
            else
            {
                removeFolder = false;
            }
        }
        return removeFolder;
    }
}