using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ksp2Redux.Tools.Common.Transactions;

public partial class InstallTransactionEngine
{
    private const string WINDOWS_PUBLICATION_AUTHORITY = "windows-publication-v1";
    private const string WINDOWS_SIBLING_AUTHORITY = "windows-publication-v2";
    [SupportedOSPlatform("windows")]
    private const AccessControlSections WINDOWS_SECURITY_SECTIONS =
        AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    [SupportedOSPlatformGuard("windows")]
    private bool IsNativeWindows => _fileSystem is Testably.Abstractions.RealFileSystem &&
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // The filesystem abstraction has no ACL-read or security-at-creation API.
    // These BCL calls are confined to the guarded real Windows filesystem bridge.
#pragma warning disable RS0030
    [SupportedOSPlatform("windows")]
    private byte[] ReadWindowsFileSecurity(string path)
    {
        Safe(path);
        return FileSystemAclExtensions.GetAccessControl(new FileInfo(path), WINDOWS_SECURITY_SECTIONS)
            .GetSecurityDescriptorBinaryForm();
    }

    [SupportedOSPlatform("windows")]
    private void RequireWindowsFileSecurity(string path, byte[] expected)
    {
        if (!WindowsFileSecurityMatches(path, expected))
            throw new InvalidDataException("Windows owner, group, DACL or descriptor control changed. Evidence retained.");
    }

    [SupportedOSPlatform("windows")]
    private bool WindowsFileSecurityMatches(string path, byte[] expected)
        => ReadWindowsFileSecurity(path).AsSpan().SequenceEqual(expected);

    [SupportedOSPlatform("windows")]
    private void CopyWindowsSecurityVerified(Stream input, string destination, Restoration.StockBaselineFile file,
        CancellationToken ct, string name, string securitySource, (InstallTransactionPlan Plan, int Index)? sibling)
    {
        Safe(destination);
        string candidate = destination;
        if (sibling is { } owned)
        {
            if (!HasWindowsSiblingAuthority(owned.Plan))
                throw new InvalidDataException("Security-copy sibling requires complete versioned ownership.");
            Restoration.StockBaselineFile old = owned.Plan.Current.Files[owned.Index];
            if (!owned.Plan.RequiresUndoBlob(old) || (file != old && file != owned.Plan.TargetFile(old.Path)))
                throw new InvalidDataException("Security-copy content differs from its finite plan slot.");
            candidate = WindowsSecurityCandidate(owned.Plan, owned.Index);
            Safe(candidate);
            RequireAbsent(candidate);
        }
        byte[] expected = ReadWindowsFileSecurity(securitySource);
        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(expected, WINDOWS_SECURITY_SECTIONS);
        Mutate($"{name}:open", () =>
        {
            // Original and parent permissions remain untouched. Only a fresh owned candidate is created.
            using FileStream output = FileSystemAclExtensions.Create(new FileInfo(candidate), FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadPermissions, FileShare.None, BUFFER_BYTES,
                FileOptions.None, security);
            VerifyStream(input, output, file, ct, name);
            Mutate($"{name}:flush", () => output.Flush(true));
        });
        RequireMatch(candidate, file);
        if (sibling is not null && !WindowsFileSecurityMatches(candidate, expected))
            Mutate($"{name}:security", () =>
            {
                Safe(candidate);
                RequireMatch(candidate, file);
                RequireWindowsFileSecurity(securitySource, expected);
                var access = new FileSecurity();
                access.SetSecurityDescriptorBinaryForm(expected, AccessControlSections.Access);
                FileSystemAclExtensions.SetAccessControl(new FileInfo(candidate), access);
            });
        RequireWindowsFileSecurity(securitySource, expected);
        RequireWindowsFileSecurity(candidate, expected);
        if (sibling is not null)
            Mutate($"{name}:transfer", () =>
            {
                Safe(candidate);
                RequireMatch(candidate, file);
                RequireWindowsFileSecurity(candidate, expected);
                RequireAbsent(destination);
                Restoration.StockArchive.PublishFile(_fileSystem, candidate, destination, false);
                FlushParent(candidate);
                FlushParent(destination);
                RequireWindowsFileSecurity(destination, expected);
            });
        RequireMatch(destination, file);
        RequireWindowsFileSecurity(securitySource, expected);
        RequireWindowsFileSecurity(destination, expected);
        FlushParent(destination);
    }
#pragma warning restore RS0030

    private byte[] WindowsPublicationAuthority(InstallTransactionPlan plan, string marker)
        => Encoding.ASCII.GetBytes($"{marker}\n{plan.GetDigest()}\n");

    private bool HasWindowsPublicationAuthority(InstallTransactionPlan plan, bool allowPartial = false)
        => HasWindowsSiblingAuthority(plan, allowPartial) || ReadWindowsAuthority(plan, WINDOWS_PUBLICATION_AUTHORITY, allowPartial);

    private bool HasWindowsSiblingAuthority(InstallTransactionPlan plan, bool allowPartial = false)
    {
        if (!IsNativeWindows)
            return false;
        if (Exists(Work(plan, WINDOWS_PUBLICATION_AUTHORITY)) && Exists(Work(plan, WINDOWS_SIBLING_AUTHORITY)))
            throw new InvalidDataException("Conflicting Windows ownership versions. Evidence retained.");
        return ReadWindowsAuthority(plan, WINDOWS_SIBLING_AUTHORITY, allowPartial);
    }

    private bool ReadWindowsAuthority(InstallTransactionPlan plan, string marker, bool allowPartial)
    {
        if (!IsNativeWindows)
            return false;
        string path = Work(plan, marker);
        Safe(path);
        if (!Exists(path))
            return false;
        byte[] expected = WindowsPublicationAuthority(plan, marker);
        byte[] actual = ReadBounded(path, expected.Length);
        RequirePrefix(actual, expected);
        if (!allowPartial && !actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Incomplete Windows publication ownership. Evidence retained.");
        return actual.AsSpan().SequenceEqual(expected);
    }

    private string WindowsPublicationCandidate(InstallTransactionPlan plan, int index)
    {
        string active = At(plan.InstallRoot, plan.Target.Files[index].Path);
        return _fileSystem.Path.Combine(_fileSystem.Path.GetDirectoryName(active)!,
            $".redux-transaction-{plan.TransactionId:N}-{index:D8}.tmp");
    }

    private IEnumerable<int> WindowsPublicationIndices(InstallTransactionPlan plan)
        => Enumerable.Range(0, plan.Target.Files.Count).Where(index =>
            !plan.RetainedUnownedFiles.Contains(plan.Target.Files[index].Path) &&
            plan.CurrentFile(plan.Target.Files[index].Path) is null);

    private string WindowsSecurityCandidate(InstallTransactionPlan plan, int index)
    {
        string active = At(plan.InstallRoot, plan.Current.Files[index].Path);
        return _fileSystem.Path.Combine(_fileSystem.Path.GetDirectoryName(active)!,
            $".redux-transaction-{plan.TransactionId:N}-security-{index:D8}.tmp");
    }

    private IEnumerable<int> WindowsSecurityIndices(InstallTransactionPlan plan)
        => Enumerable.Range(0, plan.Current.Files.Count).Where(index => plan.RequiresUndoBlob(plan.Current.Files[index]));

    private void ValidateWindowsPublicationPaths(InstallTransactionPlan plan, bool requireAbsent)
    {
        var managed = new HashSet<string>(plan.Current.Files.Concat(plan.Target.Files).Select(file => At(plan.InstallRoot, file.Path))
            .Concat(plan.Current.Directories.Concat(plan.Target.Directories).Select(path => At(plan.InstallRoot, path))),
            StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> candidates = WindowsPublicationIndices(plan).Select(index => WindowsPublicationCandidate(plan, index));
        if (requireAbsent || Exists(Work(plan, WINDOWS_SIBLING_AUTHORITY)))
            candidates = candidates.Concat(WindowsSecurityIndices(plan).Select(index => WindowsSecurityCandidate(plan, index)));
        foreach (string candidate in candidates)
        {
            DiskPath(candidate);
            if (managed.Contains(candidate))
                throw new InvalidDataException("Windows publication candidate conflicts with managed content.");
            Safe(candidate);
            if (requireAbsent)
                RequireAbsent(candidate);
        }
    }

    private void RequireWindowsPublicationCandidatesAbsent(InstallTransactionPlan plan, bool allowPartial = false)
    {
        if (!IsNativeWindows || (!Exists(Work(plan, WINDOWS_PUBLICATION_AUTHORITY)) && !Exists(Work(plan, WINDOWS_SIBLING_AUTHORITY))))
            return;
        _ = HasWindowsPublicationAuthority(plan, allowPartial);
        foreach (int index in WindowsPublicationIndices(plan))
        {
            RequireAbsent(WindowsPublicationCandidate(plan, index));
        }
        if (!allowPartial)
            RequireWindowsSecurityCandidatesAbsent(plan);
    }

    private void RequireWindowsSecurityCandidatesAbsent(InstallTransactionPlan plan)
    {
        if (!IsNativeWindows || !Exists(Work(plan, WINDOWS_SIBLING_AUTHORITY)))
            return;
        _ = HasWindowsSiblingAuthority(plan, true);
        foreach (int index in WindowsSecurityIndices(plan))
            RequireAbsent(WindowsSecurityCandidate(plan, index));
    }

    private void ValidateWindowsSecurityCandidates(InstallTransactionPlan plan, bool preparing,
        Func<Restoration.StockBaselineFile, Stream>? openTarget = null)
    {
        if (!IsNativeWindows || !Exists(Work(plan, WINDOWS_SIBLING_AUTHORITY)))
            return;
        bool owned = HasWindowsSiblingAuthority(plan, true);
        foreach (int index in WindowsSecurityIndices(plan))
        {
            string candidate = WindowsSecurityCandidate(plan, index);
            if (!owned)
            {
                RequireAbsent(candidate);
                continue;
            }
            ValidateWindowsSecurityCandidate(plan, index, preparing, openTarget);
        }
    }

    private void ValidateWindowsSecurityCandidate(InstallTransactionPlan plan, int index, bool preparing,
        Func<Restoration.StockBaselineFile, Stream>? openTarget)
    {
        string candidate = WindowsSecurityCandidate(plan, index);
        Safe(candidate);
        if (!Exists(candidate))
            return;
        Restoration.StockBaselineFile old = plan.Current.Files[index];
        string original = preparing ? At(plan.InstallRoot, old.Path) : Blob(plan, "undo", index);
        RequireMatch(original, old);
        try
        {
            ValidateRollbackCandidate(candidate, original, old);
        }
        catch (InvalidDataException) when (preparing && plan.TargetFile(old.Path) is { } target && target != old)
        {
            // Preparation may be interrupted in either the target copy or the undo copy of this slot.
            using Stream targetSource = openTarget!(target);
            ValidateRollbackCandidate(candidate, targetSource, target);
        }
    }

    private void CleanupWindowsSecurityCandidates(InstallTransactionPlan plan, bool preparing,
        Func<Restoration.StockBaselineFile, Stream>? openTarget = null)
    {
        if (!HasWindowsSiblingAuthority(plan, true))
            return;
        foreach (int index in WindowsSecurityIndices(plan))
        {
            string candidate = WindowsSecurityCandidate(plan, index);
            if (Exists(candidate))
                Mutate($"cleanup-windows-security-{index}", () =>
                {
                    Safe(candidate);
                    ValidateWindowsSecurityCandidate(plan, index, preparing, openTarget);
                    _fileSystem.File.Delete(candidate);
                    FlushParent(candidate);
                });
        }
    }

    private void ValidateWindowsPublicationCandidates(InstallTransactionPlan plan)
    {
        if (!HasWindowsPublicationAuthority(plan))
            return;
        foreach (int index in WindowsPublicationIndices(plan))
        {
            string candidate = WindowsPublicationCandidate(plan, index);
            if (!Exists(candidate))
                continue;
            string staged = Blob(plan, "target", index);
            RequireMatch(staged, plan.Target.Files[index]);
            ValidateRollbackCandidate(candidate, staged, plan.Target.Files[index]);
        }
    }

    private void CleanupWindowsPublicationCandidates(InstallTransactionPlan plan)
    {
        if (!HasWindowsPublicationAuthority(plan))
            return;
        foreach (int index in WindowsPublicationIndices(plan))
        {
            string candidate = WindowsPublicationCandidate(plan, index);
            if (Exists(candidate))
                Mutate($"cleanup-windows-publication-{index}", () =>
                {
                    _fileSystem.File.Delete(candidate);
                    FlushParent(candidate);
                });
        }
    }

    [SupportedOSPlatform("windows")]
    private void PublishWindowsManagedFile(InstallTransactionPlan plan, string staged, string active,
        Restoration.StockBaselineFile target, Restoration.StockBaselineFile? old, string? undo, int index, CancellationToken ct)
    {
        if (!HasWindowsPublicationAuthority(plan))
            throw new InvalidDataException("Windows publication requires qualified candidate ownership.");
        byte[]? expected = undo is null ? null : ReadWindowsFileSecurity(undo);
        if (old is null)
            RequireAbsent(active);
        else
        {
            RequireMatch(active, old);
            RequireWindowsFileSecurity(active, expected!);
        }
        string candidate = staged;
        if (old is null)
        {
            candidate = WindowsPublicationCandidate(plan, index);
            RequireAbsent(candidate);
            // A fresh file in the destination parent receives that parent's normal inheritance.
            CopyVerified(staged, candidate, target, ct, $"windows-candidate-{index}");
        }
        expected ??= ReadWindowsFileSecurity(candidate);
        Mutate($"install-{index}:publish", () =>
        {
            RequireMatch(candidate, target);
            RequireWindowsFileSecurity(candidate, expected);
            if (old is null)
                RequireAbsent(active);
            else
            {
                RequireMatch(active, old);
                RequireWindowsFileSecurity(active, expected);
            }
            Restoration.StockArchive.PublishFile(_fileSystem, candidate, active, old is not null);
            RequireWindowsFileSecurity(active, expected);
        });
        if (old is null)
            Mutate($"install-{index}:release-stage", () =>
            {
                // An unpublished candidate needs the stage for prefix validation. Once the rename
                // succeeds, rollback can verify and delete the exact new file in the install tree.
                RequireAbsent(candidate);
                RequireMatch(active, target);
                RequireMatch(staged, target);
                _fileSystem.File.Delete(staged);
                FlushParent(staged);
            });
    }

    private void VerifyWindowsReplacementSecurity(InstallTransactionPlan plan)
    {
        if (!IsNativeWindows || !HasWindowsPublicationAuthority(plan))
            return;
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            Restoration.StockBaselineFile old = plan.Current.Files[index];
            Restoration.StockBaselineFile? target = plan.TargetFile(old.Path);
            if (target is not null && target != old)
                RequireWindowsFileSecurity(At(plan.InstallRoot, old.Path), ReadWindowsFileSecurity(Blob(plan, "undo", index)));
        }
    }

    private void VerifyWindowsRollbackSecurity(InstallTransactionPlan plan)
    {
        if (!IsNativeWindows || !HasWindowsPublicationAuthority(plan))
            return;
        for (int index = 0; index < plan.Current.Files.Count; index++)
        {
            Restoration.StockBaselineFile old = plan.Current.Files[index];
            if (plan.IsMovedUndo(old))
                RequireAbsent(Blob(plan, "removed", index));
            if (!plan.RequiresUndoBlob(old))
                continue;
            string undo = Blob(plan, "undo", index);
            RequireMatch(undo, old);
            RequireWindowsFileSecurity(At(plan.InstallRoot, old.Path), ReadWindowsFileSecurity(undo));
        }
    }

    private void ValidateWindowsPathIdentity(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("The native transaction filesystem contract requires Windows.");
        // Query metadata without reading content. Backup semantics allow directory handles.
        using SafeFileHandle handle = OpenPath($@"\\?\{path}", 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Unable to validate native path identity at '{path}'.", new Win32Exception(Marshal.GetLastWin32Error()));
        string expected = WindowsPhysicalPathIdentity(path);
        var finalPath = new StringBuilder(32768);
        // DOS-name conversion can fail for an accessible local volume. Compare normalized GUID paths instead.
        // The separately queried volume root binds the expected drive mapping without accepting opened aliases.
        uint length = GetFinalPath(handle, finalPath, (uint)finalPath.Capacity, 1);
        if (length == 0 || length >= finalPath.Capacity)
            throw new IOException($"Unable to resolve native path identity at '{path}'.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!finalPath.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Native path alias is outside the transaction contract: '{path}'.");
    }

    private string WindowsPhysicalPathIdentity(string path)
    {
        string root = _fileSystem.Path.GetPathRoot(path)!;
        var volume = new StringBuilder(64);
        if (!GetVolumeName(root, volume, (uint)volume.Capacity))
            throw new IOException($"Unable to resolve native volume identity at '{root}'.", new Win32Exception(Marshal.GetLastWin32Error()));
        return volume.ToString() + path[root.Length..];
    }

    private static void ValidateWindowsFileStreams(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("The native transaction filesystem contract requires Windows.");
        IntPtr handle = FindFirstStream($@"\\?\{path}", 0, out StreamData data, 0);
        if (handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 38)
                return;
            throw new IOException("Unable to validate Windows file streams.", new Win32Exception(error));
        }
        try
        {
            while (true)
            {
                if (data.Name != "::$DATA")
                    throw new InvalidDataException($"Named file streams are outside the transaction byte contract: '{path}'.");
                if (FindNextStream(handle, out data))
                    continue;
                int error = Marshal.GetLastWin32Error();
                if (error != 38)
                    throw new IOException("Unable to enumerate Windows file streams.", new Win32Exception(error));
                return;
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    // The native layout is required to enumerate streams that IFileSystem does not expose.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        internal long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string Name;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, int flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr handle, out StreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenPath(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeName(string root, StringBuilder name, uint capacity);
}
