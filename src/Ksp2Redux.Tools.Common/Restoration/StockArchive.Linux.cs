using System.ComponentModel;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.Restoration;

public partial class StockArchive
{
    /// <summary>
    /// Create controlled directories and flush each newly created Linux parent entry.
    /// </summary>
    /// <remarks>
    /// The caller validates the canonical path, ownership and ancestor containment before this operation.
    /// Other filesystems retain their existing directory-creation behavior.
    /// </remarks>
    /// <param name="fileSystem">The canonical filesystem.</param>
    /// <param name="path">The controlled canonical directory path.</param>
    public static void CreateDirectory(IFileSystem fileSystem, string path)
    {
        if (fileSystem is not RealFileSystem || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            fileSystem.Directory.CreateDirectory(path);
            return;
        }
        var missing = new Stack<string>();
        string current = path;
        while (!fileSystem.Directory.Exists(current))
        {
            missing.Push(current);
            current = fileSystem.Path.GetDirectoryName(current) ?? throw new InvalidDataException("Controlled directory has no existing ancestor.");
        }
        _ = LinuxIdentity(current);
        while (missing.TryPop(out string? next))
        {
            fileSystem.Directory.CreateDirectory(next);
            _ = LinuxIdentity(next);
            FlushLinuxDirectory(fileSystem, current);
            current = next;
        }
    }
    /// <summary>
    /// Read the supported current-user Unix permission contract on a native host.
    /// </summary>
    /// <param name="fileSystem">The canonical filesystem.</param>
    /// <param name="path">The controlled existing path.</param>
    /// <returns>The validated permission bits, or null on Windows and virtual filesystems.</returns>
    public static int? ReadUnixPermissions(IFileSystem fileSystem, string path)
    {
        if (fileSystem is not RealFileSystem || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("Unix permission metadata requires a verified host adapter.");
        int mode = LinuxIdentity(path).Mode & 0xfff;
        if (mode > 511)
            throw new InvalidDataException("Special Unix permission bits require a separate metadata contract.");
        LinuxRequireMode(path, mode);
        return mode;
    }
    /// <summary>
    /// Compare the supported native Unix metadata when the host exposes that contract.
    /// </summary>
    /// <param name="fileSystem">The canonical filesystem.</param>
    /// <param name="path">The exclusively controlled path with checked ancestors.</param>
    /// <param name="mode">The expected ordinary permission bits.</param>
    /// <returns>True if native metadata matches or the filesystem has no native Unix contract, false otherwise.</returns>
    public static bool UnixPermissionsMatch(IFileSystem fileSystem, string path, int mode)
    {
        if (fileSystem is not RealFileSystem || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("Unix permission metadata requires a verified host adapter.");
        return LinuxModeMatches(path, mode);
    }

    /// <summary>
    /// Apply and flush ordinary permission metadata on the verified native Unix host.
    /// </summary>
    /// <param name="fileSystem">The canonical filesystem.</param>
    /// <param name="path">The exclusively owned path with checked ancestors.</param>
    /// <param name="mode">The authenticated ordinary permission bits.</param>
    public static void ApplyUnixPermissions(IFileSystem fileSystem, string path, int mode)
    {
        if (mode < 0 || mode > 511 || (mode & 256) == 0)
            throw new InvalidDataException("Unix permissions require ordinary bits and owner read access.");
        if (fileSystem is not RealFileSystem || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("Unix permission metadata requires a verified host adapter.");
        LinuxSetMode(path, mode);
    }

    // Linux UAPI statx offsets and size are pinned from the fixture's linux/stat.h.
    /// <summary>
    /// The stable Linux statx ABI fields consumed by path and metadata guards.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct LinuxStatus
    {
        /// <summary>
        /// The fields actually supplied by the kernel.
        /// </summary>
        [FieldOffset(0)] internal safe uint Mask;
        /// <summary>
        /// The additional inode attributes supplied by the kernel.
        /// </summary>
        [FieldOffset(8)] internal safe ulong Attributes;
        /// <summary>
        /// The inode hard-link count.
        /// </summary>
        [FieldOffset(16)] internal safe uint Links;
        /// <summary>
        /// The owner user identity.
        /// </summary>
        [FieldOffset(20)] internal safe uint User;
        /// <summary>
        /// The owner group identity.
        /// </summary>
        [FieldOffset(24)] internal safe uint Group;
        /// <summary>
        /// The file type and permission bits.
        /// </summary>
        [FieldOffset(28)] internal safe ushort Mode;
        /// <summary>
        /// The inode identity.
        /// </summary>
        [FieldOffset(32)] internal safe ulong Inode;
        /// <summary>
        /// The containing device major identity.
        /// </summary>
        [FieldOffset(136)] internal safe uint DeviceMajor;
        /// <summary>
        /// The containing device minor identity.
        /// </summary>
        [FieldOffset(140)] internal safe uint DeviceMinor;
        /// <summary>
        /// The containing mount identity.
        /// </summary>
        [FieldOffset(144)] internal safe ulong Mount;
    }

    /// <summary>
    /// Read a canonical Linux path without following its final component and reject unsafe aliases.
    /// </summary>
    /// <param name="path">The existing canonical path with independently checked ancestors.</param>
    /// <returns>The inode, mount and permission metadata returned by statx.</returns>
    internal static LinuxStatus LinuxIdentity(string path)
    {
        if (LinuxStatx(-100, path, 0x900, 0x17ff, out LinuxStatus before) != 0)
            throw LinuxFailure("inspect without following links", path);
        ValidateLinuxStatus(before, path);
        int descriptor = LinuxOpen(path, 0xA0800);
        if (descriptor < 0)
            throw LinuxFailure("open without following links", path);
        try
        {
            if (LinuxStatx(descriptor, "", 0x1100, 0x17ff, out LinuxStatus status) != 0)
                throw LinuxFailure("statx", path);
            ValidateLinuxStatus(status, path);
            if (status.Inode != before.Inode || status.Mount != before.Mount || status.DeviceMajor != before.DeviceMajor || status.DeviceMinor != before.DeviceMinor)
                throw new InvalidDataException($"Linux path identity changed while opening '{path}'.");
            var buffer = new byte[32768];
            nint length = LinuxReadLink($"/proc/self/fd/{descriptor}", buffer, (nuint)buffer.Length);
            if (length <= 0 || length >= buffer.Length || Encoding.UTF8.GetString(buffer, 0, (int)length) != path)
                throw new InvalidDataException($"Linux path does not resolve to its canonical declared identity: '{path}'.");
            return status;
        }
        finally { _ = LinuxClose(descriptor); }
    }

    /// <summary>
    /// Validate the supported owner, group, ordinary permissions and absence of extended metadata.
    /// </summary>
    /// <param name="path">The controlled native path.</param>
    /// <param name="mode">The authenticated Unix permission bits.</param>
    internal static void LinuxRequireMode(string path, int mode)
    {
        if (!LinuxModeMatches(path, mode))
            throw new InvalidDataException($"Linux ownership or permissions differ from the contract: '{path}'.");
    }

    /// <summary>
    /// Compare ordinary permissions and the current user's supported ownership contract.
    /// </summary>
    /// <param name="path">The controlled native path.</param>
    /// <param name="mode">The authenticated permission bits.</param>
    /// <returns>True if ownership and permissions match, false otherwise.</returns>
    internal static bool LinuxModeMatches(string path, int mode)
    {
        LinuxStatus status = LinuxIdentity(path);
        if (status.Attributes != 0)
            throw new InvalidDataException($"Linux inode attributes require a separate metadata contract: '{path}'.");
        int descriptor = LinuxOpen(path, 0xA0800);
        if (descriptor < 0)
            throw LinuxFailure("open metadata", path);
        try
        {
            nint length = LinuxListAttributes(descriptor, IntPtr.Zero, 0);
            if (length < 0)
                throw LinuxFailure("list extended metadata", path);
            if (length != 0)
                throw new InvalidDataException($"Linux extended attributes and ACLs require a separate metadata contract: '{path}'.");
        }
        finally { _ = LinuxClose(descriptor); }
        return (status.Mode & 0xfff) == mode && status.User == LinuxUser() && status.Group == LinuxGroup();
    }

    /// <summary>
    /// Apply and flush explicit ordinary Linux permissions on an owned path.
    /// </summary>
    /// <param name="path">The exclusively owned existing path.</param>
    /// <param name="mode">The authenticated permission bits.</param>
    internal static void LinuxSetMode(string path, int mode)
    {
        LinuxStatus current = LinuxIdentity(path);
        if ((current.Mode & 0xe00) != 0)
            throw new InvalidDataException("Existing special Unix permission bits require a separate metadata contract.");
        if (mode < 0 || mode > 511 || (mode & 256) == 0 ||
            ((current.Mode & 0xf000) == 0x4000 && (mode & 448) != 448))
            throw new InvalidDataException("Unsupported Unix permissions cannot be applied to a controlled path.");
        LinuxRequireMode(path, current.Mode & 0xfff);
        int descriptor = LinuxOpen(path, 0xA0800);
        if (descriptor < 0)
            throw LinuxFailure("open for permissions", path);
        try
        {
            if (LinuxChmod(descriptor, (uint)mode) != 0 || LinuxSync(descriptor) != 0)
                throw LinuxFailure("set and flush permissions", path);
        }
        finally { _ = LinuxClose(descriptor); }
        LinuxRequireMode(path, mode);
    }

    /// <summary>
    /// Flush directory metadata after a Linux namespace mutation.
    /// </summary>
    /// <param name="fileSystem">The canonical filesystem.</param>
    /// <param name="path">The affected directory.</param>
    public static void FlushLinuxDirectory(IFileSystem fileSystem, string path)
    {
        if (fileSystem is not RealFileSystem || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        _ = LinuxIdentity(path);
        int descriptor = LinuxOpen(path, 0xB0000);
        if (descriptor < 0)
            throw LinuxFailure("open directory for flush", path);
        try
        {
            if (LinuxSync(descriptor) != 0)
                throw LinuxFailure("flush directory", path);
        }
        finally { _ = LinuxClose(descriptor); }
    }

    private static IOException LinuxFailure(string operation, string path) =>
        new($"Linux {operation} failed at '{path}'.", new Win32Exception(Marshal.GetLastWin32Error()));

    private static void ValidateLinuxStatus(LinuxStatus status, string path)
    {
        if ((status.Mask & 0x111f) != 0x111f || (status.Mode & 0xf000) is not (0x4000 or 0x8000) ||
            ((status.Mode & 0xf000) == 0x8000 && status.Links != 1))
            throw new InvalidDataException($"Linux path type, hard links or required identity fields are unsupported: '{path}'.");
    }

    private static void PublishLinuxFile(IFileSystem fileSystem, string source, string destination, bool overwrite)
    {
        _ = LinuxIdentity(source);
        if (LinuxRename(-100, source, -100, destination, overwrite ? 0u : 1u) != 0)
            throw LinuxFailure("atomic publication", destination);
        string sourceParent = fileSystem.Path.GetDirectoryName(source)!;
        string destinationParent = fileSystem.Path.GetDirectoryName(destination)!;
        FlushLinuxDirectory(fileSystem, sourceParent);
        if (sourceParent != destinationParent)
            FlushLinuxDirectory(fileSystem, destinationParent);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static safe extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static safe extern int LinuxClose(int descriptor);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static safe extern int LinuxStatx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatus result);
    [DllImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static safe extern nint LinuxReadLink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer, nuint length);
    [DllImport("libc", EntryPoint = "flistxattr", SetLastError = true)]
    private static safe extern nint LinuxListAttributes(int descriptor, IntPtr buffer, nuint length);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static safe extern int LinuxChmod(int descriptor, uint mode);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static safe extern int LinuxSync(int descriptor);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static safe extern uint LinuxUser();
    [DllImport("libc", EntryPoint = "getegid")]
    private static safe extern uint LinuxGroup();
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static safe extern int LinuxRename(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int targetDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);
}
