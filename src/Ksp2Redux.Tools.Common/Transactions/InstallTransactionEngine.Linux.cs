using System.Runtime.InteropServices;
using Ksp2Redux.Tools.Common.Restoration;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.Transactions;

public partial class InstallTransactionEngine
{
    private bool IsNativeLinux => _fileSystem is RealFileSystem && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private void ValidateNativePathIdentity(string path)
    {
        if (IsNativeLinux)
            _ = StockArchive.LinuxIdentity(path);
        else
            ValidateWindowsPathIdentity(path);
    }

    private static void ValidateNativeFileStreams(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            _ = StockArchive.LinuxIdentity(path);
        else
            ValidateWindowsFileStreams(path);
    }

    private List<(string Inode, string Relative)> LinuxAnchors(string path)
    {
        var anchors = new List<(string Inode, string Relative)>();
        string relative = "";
        for (string? current = path; current is not null; current = _fileSystem.Path.GetDirectoryName(current))
        {
            if (_fileSystem.Path.Exists(current))
            {
                var identity = StockArchive.LinuxIdentity(current);
                anchors.Add(($"{identity.DeviceMajor}:{identity.DeviceMinor}:{identity.Inode}", relative));
            }
            relative = _fileSystem.Path.GetFileName(current) + (relative.Length == 0 ? "" : "/" + relative);
        }
        return anchors;
    }

    private bool LinuxOverlaps(string first, string second)
    {
        var rightAnchors = LinuxAnchors(second);
        foreach (var left in LinuxAnchors(first))
        {
            foreach (var right in rightAnchors)
            {
                if (left.Inode == right.Inode && (left.Relative.Length == 0 || right.Relative.Length == 0 ||
                    left.Relative == right.Relative || left.Relative.StartsWith(right.Relative + "/", StringComparison.Ordinal) ||
                    right.Relative.StartsWith(left.Relative + "/", StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    private void ValidateLinuxRoots(InstallTransactionPlan plan)
    {
        if (!IsNativeLinux)
            return;
        if (!plan.Current.HasUnixMetadata || !plan.Target.HasUnixMetadata || !plan.Stock.HasUnixMetadata)
            throw new InvalidDataException("Native Linux transactions require explicit version-two Unix metadata.");
        string parent = _fileSystem.Path.GetDirectoryName(plan.TransactionRoot)!;
        var installation = StockArchive.LinuxIdentity(plan.InstallRoot);
        var transaction = StockArchive.LinuxIdentity(parent);
        if (installation.Mount != transaction.Mount || installation.DeviceMajor != transaction.DeviceMajor ||
            installation.DeviceMinor != transaction.DeviceMinor)
            throw new InvalidDataException("Transaction storage must use the exact installation mount.");
        foreach (string directory in plan.Current.Directories.Intersect(plan.Target.Directories))
        {
            if (plan.Current.UnixDirectoryModes[directory] != plan.Target.UnixDirectoryModes[directory])
                throw new NotSupportedException("Changing existing Unix directory permissions requires a directory metadata transaction.");
        }
    }

    private void SetLinuxMode(string path, int? mode)
    {
        if (IsNativeLinux && mode is not null)
            StockArchive.LinuxSetMode(path, mode.Value);
    }

    private void RequireLinuxDirectoryMode(string path, StockBaselineManifest manifest, string relative)
    {
        if (IsNativeLinux)
            StockArchive.LinuxRequireMode(path, manifest.UnixDirectoryModes[relative]);
    }

    private void FlushParent(string path) =>
        StockArchive.FlushLinuxDirectory(_fileSystem, _fileSystem.Path.GetDirectoryName(path)!);

    private void CreateDirectory(string path)
    {
        StockArchive.CreateDirectory(_fileSystem, path);
    }
}
