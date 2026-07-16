using System;
using System.IO;
using System.IO.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services;

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
        try
        {
            var fullPath = fileSystem.Path.GetFullPath(path);
            var root = fileSystem.Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return null;

            // On Unix the path root is always "/", which can be a different (and much smaller) mount
            // than the one actually holding the path - e.g. SteamOS has a ~5 GB rootfs while games
            // live on the separate /home partition. Unix DriveInfo reports on the filesystem of
            // exactly the path it is given, so query the deepest existing directory of the path.
            // Windows DriveInfo only accepts drive roots, so keep querying the root there.
            var query = root;
            if (root == "/")
            {
                var probe = fullPath;
                while (!string.IsNullOrEmpty(probe) && !fileSystem.Directory.Exists(probe))
                    probe = fileSystem.Path.GetDirectoryName(probe);
                if (!string.IsNullOrEmpty(probe)) query = probe;
            }

            return fileSystem.DriveInfo.New(query).AvailableFreeSpace;
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
