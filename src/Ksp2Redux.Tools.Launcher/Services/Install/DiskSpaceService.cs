using System;
using System.IO;
using System.IO.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public interface IDiskSpaceService
{
    /// <summary>
    /// Returns the free space available on the drive containing <paramref name="path"/>, in bytes.
    /// Returns null if the drive could not be determined or queried.
    /// </summary>
    long? GetAvailableFreeSpace(string path);
}

public class DiskSpaceService(IFileSystem fileSystem) : IDiskSpaceService
{
    public long? GetAvailableFreeSpace(string path)
    {
        var root = fileSystem.Path.GetPathRoot(fileSystem.Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return null;

        try
        {
            return fileSystem.DriveInfo.New(root).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
