using System.ComponentModel;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.Restoration;

public partial class StockArchive
{
    /// <summary>
    /// Publish verified file bytes with a same-filesystem native rename, without a copy fallback.
    /// </summary>
    /// <remarks>
    /// The caller validates paths and expected destination bytes and excludes concurrent writers.
    /// POSIX archive publication requires sibling paths in a caller-owned stable parent.
    /// Virtual filesystem adapters must provide equivalent rename semantics through IFile.Move.
    /// </remarks>
    /// <param name="fileSystem">The filesystem used for validated file operations.</param>
    /// <param name="source">The verified candidate path.</param>
    /// <param name="destination">The validated publication path.</param>
    /// <param name="overwrite">True to replace a verified destination, false to require absence.</param>
    /// <exception cref="IOException">Thrown when the rename fails, including cross-volume publication.</exception>
    public static void PublishFile(IFileSystem fileSystem, string source, string destination, bool overwrite)
    {
        if (fileSystem is not RealFileSystem)
        {
            if (overwrite)
                fileSystem.File.Move(source, destination, true);
            else
                fileSystem.File.Move(source, destination);
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // File.Move in .NET 10 enables MOVEFILE_COPY_ALLOWED, even with overwrite.
            // The native call deliberately omits that flag and uses validated extended local paths.
            if (!MoveFileEx($@"\\?\{source}", $@"\\?\{destination}", overwrite ? 1u : 0u))
                throw new IOException("Same-volume file publication failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            PublishLinuxFile(fileSystem, source, destination, overwrite);
            return;
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("Native archive publication requires Windows, Linux or macOS.");
        if (fileSystem.Path.GetDirectoryName(source) != fileSystem.Path.GetDirectoryName(destination))
            throw new InvalidDataException("POSIX archive publication requires sibling paths.");
        if (!overwrite && (fileSystem.File.Exists(destination) || fileSystem.Directory.Exists(destination)))
            throw new IOException("The publication destination already exists.");
        // rename reports EXDEV instead of copying. Caller ownership excludes racing creators.
        if (Rename(source, destination) != 0)
            throw new IOException("Same-filesystem archive publication failed.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static safe extern bool MoveFileEx(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "rename", ExactSpelling = true, SetLastError = true)]
    private static safe extern int Rename([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
