using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Microsoft.Win32.SafeHandles;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

public partial class InstallTransactionEngineTest
{
    /// <summary>
    /// Canonical new local roots on both actual volumes pass the production path guard before creation.
    /// </summary>
    /// <returns>The native fixture verification task.</returns>
    [Test, Platform("Win")]
    public async Task NativeRootAdmissionProbe_ValidatesFreshOwnedRootsAsync()
    {
        string? selected = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_CROSS_VOLUME_FIXTURE_ROOT");
        if (string.IsNullOrWhiteSpace(selected))
        {
            Assert.Ignore("Supply a fresh owned second-volume fixture through the maintained runner.");
            return;
        }
        var fs = new RealFileSystem();
        string workspace = fs.Path.GetDirectoryName(typeof(InstallTransactionEngineTest).Assembly.Location)!;
        while (!fs.Directory.Exists(fs.Path.Combine(workspace, "Documentation", "Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("The evidence checkout was not found.");
        }
        string root = fs.Path.Combine(workspace, "Library", "Omnipatching", "launcher-preparation", $"root-admission-{Guid.NewGuid():N}");
        string second = fs.Path.GetFullPath(selected);
        if (second != selected || fs.Path.GetPathRoot(second) == fs.Path.GetPathRoot(root) ||
            !System.Text.RegularExpressions.Regex.IsMatch(fs.Path.GetFileName(second), "^redux-publication-test-[a-f0-9]{32}$") ||
            fs.Path.Exists(second))
            throw new InvalidDataException("The probe requires an explicitly owned fresh canonical second-volume root.");
        for (string? parent = fs.Path.GetDirectoryName(second); parent is not null; parent = fs.Path.GetDirectoryName(parent))
        {
            if ((fs.File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The explicitly owned fixture cannot traverse a reparse ancestor.");
        }
        var engine = new InstallTransactionEngine(fs, _ => long.MaxValue);
        var timer = Stopwatch.StartNew();
        long allocations = GC.GetTotalAllocatedBytes();
        var failures = new List<object>();
        var observations = new List<object>();
        var guards = new List<object>();
        object? generation = null;
        foreach (string volume in new[] { fs.Path.GetPathRoot(root)!, fs.Path.GetPathRoot(second)! })
        {
            foreach (uint flags in new uint[] { 0, 8, 1, 9, 2, 10, 4, 12 })
            {
                observations.Add(ObserveNativeIdentity(volume, flags));
            }
        }
        foreach (string path in new[] { root, second })
        {
            try { engine.ValidateControlledPath(path); }
            catch (Exception failure)
            {
                failures.Add(new { Path = path, Phase = "before-create", Type = failure.GetType().FullName,
                    failure.Message, NativeError = (failure.InnerException as Win32Exception)?.NativeErrorCode });
            }
        }
        fs.Directory.CreateDirectory(root);
        fs.Directory.CreateDirectory(second);
        foreach (string path in new[] { root, second })
        {
            string file = fs.Path.Combine(path, "identity.bin");
            fs.File.WriteAllBytes(file, [1, 2, 3, 4]);
            observations.Add(ObserveNativeIdentity(path, 0));
            observations.Add(ObserveNativeIdentity(file, 0));
            try
            {
                engine.ValidateControlledPath(path);
                engine.ValidateControlledPath(file);
                engine.ValidateControlledPath(fs.Path.Combine(path, "not-created", "child.bin"));
            }
            catch (Exception failure)
            {
                failures.Add(new { Path = path, Phase = "existing-and-missing-child", Type = failure.GetType().FullName,
                    failure.Message, NativeError = (failure.InnerException as Win32Exception)?.NativeErrorCode });
            }
        }
        if (failures.Count == 0)
        {
            try
            {
                foreach (string path in new[] { root, second })
                {
                    string file = fs.Path.Combine(path, "identity.bin");
                    Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.Combine(path, "IDENTITY.bin")));
                    Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.Combine(path, "uncreated", "..", "identity.bin")));
                    Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.GetPathRoot(path)!));
                    string shortPath = ShortWindowsPath(path);
                    bool shortAlias = !shortPath.Equals(path, StringComparison.OrdinalIgnoreCase);
                    if (shortAlias)
                        Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(shortPath));
                    string streamFile = fs.Path.Combine(path, "named-stream.bin");
                    fs.File.WriteAllBytes(streamFile, [7, 8, 9]);
                    fs.File.WriteAllBytes(streamFile + ":owned-fixture-metadata", [10, 11]);
                    Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(streamFile));
                    string link = fs.Path.Combine(path, "owned-reparse");
                    string linkTarget = fs.Path.Combine(path, "owned-link-target");
                    fs.Directory.CreateDirectory(linkTarget);
                    fs.Directory.CreateSymbolicLink(link, linkTarget);
                    Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(link));
                    Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.Combine(link, "identity.bin")));
                    Assert.That(fs.File.ReadAllBytes(file), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                    guards.Add(new { Root = path, CaseAliasRejected = true, TraversalRejected = true,
                        VolumeRootRejected = true, NamedStreamRejected = true, ReparseAndDescendantRejected = true,
                        ShortAliasAvailable = shortAlias, ShortAliasRejected = shortAlias });
                }
                generation = await VerifyOwnedCrossVolumeGenerationAsync(fs, root, second);
            }
            catch (Exception failure)
            {
                failures.Add(new { Path = second, Phase = "guards-and-native-generation", Type = failure.GetType().FullName,
                    failure.Message, NativeError = (failure.InnerException as Win32Exception)?.NativeErrorCode });
            }
        }
        string report = fs.Path.Combine(root, "result.json");
        using var process = Process.GetCurrentProcess();
        fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Success = failures.Count == 0, NativeWindows = true, FullGame = false,
            FirstRoot = root, SecondRoot = second, Failures = failures, NativeIdentities = observations,
            GuardResults = guards, CrossVolumeGeneration = generation, PagingAllowed = true,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            CumulativeManagedAllocatedBytes = GC.GetTotalAllocatedBytes() - allocations,
            ProcessLifetimePeakWorkingSetBytes = process.PeakWorkingSet64
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(report, "Production root admission and native namespace observations on new owned fixtures.");
        TestContext.Progress.WriteLine($"Native path admission evidence: {report}");
        Assert.That(failures, Is.Empty, "Production native identity must admit canonical fresh local roots on both volumes.");
    }

    private static async Task<object> VerifyOwnedCrossVolumeGenerationAsync(RealFileSystem fs, string root, string second)
    {
        string stockRoot = fs.Path.Combine(root, "stock");
        string source = fs.Path.Combine(root, "target");
        string install = fs.Path.Combine(second, "game");
        var originalFiles = new Dictionary<string, string>
            { ["game.exe"] = "owned original executable", ["StreamingAssets/content.bin"] = "owned original content" };
        var targetFiles = new Dictionary<string, string>
            { ["game.exe"] = "owned target executable", ["StreamingAssets/content.bin"] = "owned target content" };
        foreach (var file in originalFiles)
        {
            Put(fs, stockRoot, file.Key, Encoding.UTF8.GetBytes(file.Value));
            Put(fs, install, file.Key, Encoding.UTF8.GetBytes(file.Value));
        }
        foreach (var file in targetFiles)
        {
            Put(fs, source, file.Key, Encoding.UTF8.GetBytes(file.Value));
        }
        Put(fs, install, USER, USER_BYTES);
        var stock = Manifest("native-root-stock", originalFiles);
        var target = Manifest("native-root-target", targetFiles);
        string archivePath = fs.Path.Combine(install, "uninstall.zip");
        var archive = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        await archive.CreateAsync(stockRoot, archivePath, stock);
        byte[] archiveBytes = fs.File.ReadAllBytes(archivePath);
        var reference = new StockBaselineFile(archivePath, archiveBytes.Length, Digest(archiveBytes));
        var plan = new InstallTransactionPlan(Guid.NewGuid(), install, source, fs.Path.Combine(second, "work-apply"),
            stock, target, stock, reference, useChangedFileStaging: true);
        var engine = new InstallTransactionEngine(fs, _ => long.MaxValue);
        Assert.That(engine.Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
        AssertNativeRootTree(fs, install, target);
        Assert.That(new InstallTransactionEngine(fs, _ => long.MaxValue).Recover(plan), Is.EqualTo(InstallTransactionResult.Committed));
        string restored = fs.Path.Combine(root, "restored-stock");
        await archive.RestoreToNewDirectoryAsync(archivePath, stock, restored);
        var restore = new InstallTransactionPlan(Guid.NewGuid(), install, restored, fs.Path.Combine(second, "work-restore"),
            target, stock, stock, reference, useChangedFileStaging: true);
        Assert.That(engine.Apply(restore), Is.EqualTo(InstallTransactionResult.Committed));
        AssertNativeRootTree(fs, install, stock);
        await archive.VerifyAsync(archivePath, stock);
        Assert.That(fs.File.ReadAllBytes(archivePath), Is.EqualTo(archiveBytes));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(install, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));
        return new
        {
            SourceRoot = source, Installation = install, RestoredStockRoot = restored,
            NativeApplyCommitted = true, FreshCommittedRecoveryVerified = true, NativeRestoreCommitted = true,
            StockArchiveIncludesStreamingAssets = true, ArchiveUnchanged = true, UnknownUserDataPreserved = true,
            ArchiveSha256 = reference.Sha256, TargetFiles = target.Files, RestoredFiles = stock.Files,
            StockBytes = stock.TotalBytes, TargetBytes = target.TotalBytes,
            FullGame = false, ProductionCoordinatorVerified = false
        };
    }

    private static void AssertNativeRootTree(RealFileSystem fs, string root, StockBaselineManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            byte[] bytes = fs.File.ReadAllBytes(fs.Path.Combine(root, file.Path.Replace('/', '\\')));
            Assert.That(bytes.Length, Is.EqualTo(file.Length));
            Assert.That(Digest(bytes), Is.EqualTo(file.Sha256));
        }
        foreach (string directory in manifest.Directories)
        {
            Assert.That(fs.Directory.Exists(fs.Path.Combine(root, directory.Replace('/', '\\'))), Is.True);
        }
    }

    private static object ObserveNativeIdentity(string path, uint flags)
    {
        using SafeFileHandle handle = OpenIdentityProbePath($@"\\?\{path}", 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
            return new { Path = path, Flags = flags, OpenError = Marshal.GetLastWin32Error(), Length = 0, FinalPath = "", FinalError = 0 };
        var result = new StringBuilder(32768);
        uint length = ReadIdentityProbePath(handle, result, (uint)result.Capacity, flags);
        int error = length == 0 ? Marshal.GetLastWin32Error() : 0;
        return new { Path = path, Flags = flags, OpenError = 0, Length = length, FinalPath = result.ToString(), FinalError = error };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenIdentityProbePath(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint ReadIdentityProbePath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
