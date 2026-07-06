using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Common.Service;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface ICacheService
{
    List<string> IgnoredDirectories { get; }
    void RecursivelyCreateCache(string directory);
    void AddFolder(IZipArchive archive, string directory, string prefix);
    void RecursivelyRestoreCache(string directory, bool isForRepatch=false);
}

public class CacheService(IFileSystem fileSystem, IZipFileService zipFileService) : ICacheService
{
    public List<string> IgnoredDirectories
        => [fileSystem.Path.Combine("KSP2_x64_Data","StreamingAssets"), "UninstallTemp", "mods"];

    public List<string> SavedDirectories = ["Redux/Config"];

    // Paths (relative to the install dir) that are always deleted during a restore, even if they
    // fall under an IgnoredDirectories subtree. Used for Redux-produced artifacts that live inside
    // otherwise-ignored stock folders (e.g. baked subscenes under StreamingAssets).
    public List<string> PurgeOnRestore =>
    [
        fileSystem.Path.Combine("KSP2_x64_Data", "StreamingAssets", "EntityScenes"),
    ];


    public void RecursivelyCreateCache(string directory)
    {
        if (fileSystem.File.Exists(fileSystem.Path.Combine(directory, "uninstall.zip")))
        {
            fileSystem.File.Delete(fileSystem.Path.Combine(directory, "uninstall.zip"));
        }

        var tempFile = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), $"uninstall-{Guid.CreateVersion7()}.zip");
        try
        {
            using (var saveStream = fileSystem.File.Open(tempFile, FileMode.Create, FileAccess.Write))
            {
                using (var zipArchive = zipFileService.NewArchive(saveStream, ZipArchiveMode.Create, false))
                {
                    AddFolder(zipArchive, directory, "");
                }
            }

            fileSystem.File.Move(tempFile, fileSystem.Path.Combine(directory, "uninstall.zip"));
        }
        catch
        {
            // A partial snapshot (locked file, full disk) would otherwise sit in the temp folder
            // forever, silently eating disk space across repeated failed attempts.
            try { if (fileSystem.File.Exists(tempFile)) fileSystem.File.Delete(tempFile); } catch { }
            throw;
        }
    }

    public void AddFolder(IZipArchive archive, string directory, string prefix)
    {
        if (IgnoredDirectories.Contains(prefix))
        {
            return;
        }
        var dir = fileSystem.DirectoryInfo.New(directory);
        foreach (var file in dir.GetFiles())
        {
            if (file.Name == "uninstall.zip" && prefix == "") continue;
            archive.CreateEntryFromFile(file.FullName, fileSystem.Path.Combine(prefix, file.Name));
        }

        foreach (var subDir in dir.GetDirectories())
        {
            AddFolder(archive, fileSystem.Path.Combine(directory, subDir.Name), fileSystem.Path.Combine(prefix, subDir.Name));
        }
    }
    
    // If isForRepatch is true, then the Redux/Config folder is saved, and 
    public void RecursivelyRestoreCache(string directory, bool isForRepatch=false)
    {
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(directory, "uninstall.zip")))
        {
            throw new CacheRestoreException(directory, "uninstall.zip");
        }

        if (isForRepatch)
        {
            fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(directory, "UninstallTemp"));
            foreach (var dir in SavedDirectories)
            {
                if (fileSystem.Directory.Exists(fileSystem.Path.Combine(directory, dir)))
                {
                    var parentDirectory = fileSystem.DirectoryInfo.New(fileSystem.Path.Combine(directory, "UninstallTemp", dir)).Parent!;
                    if (!parentDirectory.Exists)
                    {
                        parentDirectory.Create();
                    }
                    fileSystem.Directory.Move(fileSystem.Path.Combine(directory, dir), fileSystem.Path.Combine(directory, "UninstallTemp", dir));
                }
            }
        }
        
        foreach (var relative in PurgeOnRestore)
        {
            var full = fileSystem.Path.Combine(directory, relative);
            if (fileSystem.Directory.Exists(full))
            {
                fileSystem.Directory.Delete(full, true);
            }
            else if (fileSystem.File.Exists(full))
            {
                fileSystem.File.Delete(full);
            }
        }

        ClearOutFolder(directory);

        using (var zipFile = zipFileService.OpenRead(fileSystem.Path.Combine(directory, "uninstall.zip")))
        {
            zipFileService.ExtractToDirectory(zipFile, directory, true);
        }
        
        if (!isForRepatch)
        {
            fileSystem.File.Delete(fileSystem.Path.Combine(directory, "uninstall.zip"));
        }
        else
        {
            foreach (var dir in SavedDirectories)
            {
                if (fileSystem.Directory.Exists(fileSystem.Path.Combine(directory, "UninstallTemp", dir)))
                {
                    var parentDirectory = fileSystem.DirectoryInfo.New(fileSystem.Path.Combine(directory, dir)).Parent!;
                    if (!parentDirectory.Exists)
                    {
                        parentDirectory.Create();
                    }
                    fileSystem.Directory.Move(fileSystem.Path.Combine(directory, "UninstallTemp", dir), fileSystem.Path.Combine(directory, dir));
                }
            }
            fileSystem.Directory.Delete(fileSystem.Path.Combine(directory, "UninstallTemp"), true);
        }
    }

    private bool ClearOutFolder(string dir, string prefix="")
    {
        if (IgnoredDirectories.Contains(prefix))
        {
            return false;
        }

        var info = fileSystem.DirectoryInfo.New(dir);
        foreach (var file in info.GetFiles())
        {
            if (file.Name == "uninstall.zip" && prefix == "") continue;
            file.Delete();
        }

        bool removeFolder = true;
        foreach (var directory in info.GetDirectories())
        {
            var prefix2 = fileSystem.Path.Combine(prefix, directory.Name);
            var dir2 = fileSystem.Path.Combine(dir, directory.Name);
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