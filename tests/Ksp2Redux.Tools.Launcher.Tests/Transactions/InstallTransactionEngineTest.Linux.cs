using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

public partial class InstallTransactionEngineTest
{
    /// <summary>
    /// Native Linux installation and full fixture archive restoration use the canonical transaction engine.
    /// </summary>
    /// <returns>The fixture task.</returns>
    [Test, Platform("Linux")]
    public async Task NativeLinux_ApplyAndRestoreAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException();
        string root = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_LINUX_FIXTURE_ROOT")
            ?? throw new InvalidOperationException("Run the maintained owned native Linux fixture workflow.");
        var fs = new RealFileSystem();
        if (!root.StartsWith("/home/", StringComparison.Ordinal) || fs.Path.Exists(root))
            throw new InvalidDataException("The native fixture must be a fresh owned home-directory root.");
        fs.Directory.CreateDirectory(root);
        string install = fs.Path.Combine(root, "game");
        string source = fs.Path.Combine(root, "source");
        string stockRoot = fs.Path.Combine(root, "stock");
        var original = new Dictionary<string, string> { ["game.exe"] = "owned stock", ["StreamingAssets/content.bin"] = "owned stock asset" };
        var changed = new Dictionary<string, string> { ["game.exe"] = "owned target", ["StreamingAssets/content.bin"] = "owned rebuilt asset" };
        foreach (var file in original)
        {
            LinuxPut(fs, stockRoot, file.Key, file.Value);
            LinuxPut(fs, install, file.Key, file.Value);
        }
        foreach (var file in changed)
        {
            LinuxPut(fs, source, file.Key, file.Value);
        }
        LinuxPut(fs, install, "mods/user.txt", "unknown user bytes");
        StockBaselineManifest stock = LinuxManifest("linux-owned-stock", original);
        StockBaselineManifest target = LinuxManifest("linux-owned-target", changed);
        string archivePath = fs.Path.Combine(install, "uninstall.zip");
        var archive = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        bool success = false;
        string? failureText = null;
        try
        {
            await archive.CreateAsync(stockRoot, archivePath, stock);
            byte[] archiveBytes = fs.File.ReadAllBytes(archivePath);
            var reference = new StockBaselineFile(archivePath, archiveBytes.Length, LinuxHash(archiveBytes));
            var plan = new InstallTransactionPlan(Guid.NewGuid(), install, source, fs.Path.Combine(root, "work"), stock, target, stock,
                reference, useChangedFileStaging: true);
            var engine = new InstallTransactionEngine(fs, _ => long.MaxValue);
            Assert.That(engine.Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
            Assert.That(new InstallTransactionEngine(fs, _ => long.MaxValue).Recover(plan), Is.EqualTo(InstallTransactionResult.Committed));
            foreach (var file in changed)
            {
                Assert.That(fs.File.ReadAllText(fs.Path.Combine(install, file.Key)), Is.EqualTo(file.Value));
                Assert.That((int)fs.File.GetUnixFileMode(fs.Path.Combine(install, file.Key)), Is.EqualTo(file.Key == "game.exe" ? 493 : 420));
            }
            string restoreRoot = fs.Path.Combine(root, "restored");
            await archive.RestoreToNewDirectoryAsync(archivePath, stock, restoreRoot);
            var restore = new InstallTransactionPlan(Guid.NewGuid(), install, restoreRoot, fs.Path.Combine(root, "restore-work"),
                target, stock, stock, reference, useChangedFileStaging: true);
            Assert.That(engine.Apply(restore), Is.EqualTo(InstallTransactionResult.Committed));
            foreach (var file in original)
            {
                Assert.That(fs.File.ReadAllText(fs.Path.Combine(install, file.Key)), Is.EqualTo(file.Value));
                Assert.That((int)fs.File.GetUnixFileMode(fs.Path.Combine(install, file.Key)), Is.EqualTo(file.Key == "game.exe" ? 448 : 420));
            }
            Assert.That(fs.File.ReadAllBytes(archivePath), Is.EqualTo(archiveBytes));
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(install, "mods/user.txt")), Is.EqualTo("unknown user bytes"));
            success = true;
        }
        catch (Exception failure)
        {
            failureText = failure.ToString();
            throw;
        }
        finally
        {
            fs.File.WriteAllText(fs.Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
                { Success = success, Failure = failureText, NativeLinux = true, Root = root, FullGame = false }));
        }
    }

    private static void LinuxPut(RealFileSystem fs, string root, string relative, string text)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException();
        string path = fs.Path.Combine(root, relative);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
        fs.File.WriteAllText(path, text);
        fs.File.SetUnixFileMode(path, (UnixFileMode)(relative == "game.exe" ? (text.Contains("target", StringComparison.Ordinal) ? 493 : 448) : 420));
    }

    private static StockBaselineManifest LinuxManifest(string identity, Dictionary<string, string> files)
    {
        var baseline = new StockBaselineManifest(identity, "Owned synthetic native Linux fixture", files.Select(file => new StockBaselineFile(file.Key,
            Encoding.UTF8.GetByteCount(file.Value), LinuxHash(Encoding.UTF8.GetBytes(file.Value)),
            file.Key == "game.exe" ? (identity.Contains("target", StringComparison.Ordinal) ? 493 : 448) : 420)),
            unixDirectoryModes: new Dictionary<string, int> { ["StreamingAssets"] = 493 });
        Assert.That(StockBaselineManifest.FromJson(baseline.ToJson()).ToJson(), Is.EqualTo(baseline.ToJson()));
        return baseline;
    }

    private static string LinuxHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Applying ordinary permissions rejects existing special bits without changing the owned witness.
    /// </summary>
    /// <param name="specialBit">The existing setuid, setgid or sticky permission bit.</param>
    [TestCase(2048), TestCase(1024), TestCase(512), Platform("Linux")]
    public void NativeLinux_ExistingSpecialPermissionsRemainUnchanged(int specialBit)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException();
        string root = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_LINUX_FIXTURE_ROOT")!
            + "-special-" + specialBit;
        var fs = new RealFileSystem();
        Assert.That(fs.Path.Exists(root), Is.False);
        fs.Directory.CreateDirectory(root);
        string path = fs.Path.Combine(root, "unexecuted-witness.bin");
        byte[] original = Encoding.UTF8.GetBytes("owned special-permission witness, never executed");
        fs.File.WriteAllBytes(path, original);
        int mode = 493 | specialBit;
        fs.File.SetUnixFileMode(path, (UnixFileMode)mode);
        Assert.That((int)fs.File.GetUnixFileMode(path), Is.EqualTo(mode));
        bool rejected = false;
        try
        {
            Assert.Throws<InvalidDataException>(() => StockArchive.ApplyUnixPermissions(fs, path, 493));
            rejected = true;
            Assert.That((int)fs.File.GetUnixFileMode(path), Is.EqualTo(mode));
            Assert.That(fs.File.ReadAllBytes(path), Is.EqualTo(original));
        }
        finally
        {
            int actualMode = (int)fs.File.GetUnixFileMode(path);
            string actualHash = LinuxHash(fs.File.ReadAllBytes(path));
            fs.File.WriteAllText(fs.Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                Success = rejected && actualMode == mode && actualHash == LinuxHash(original),
                NativeLinux = true, ExistingMode = mode, ActualMode = actualMode, Rejected = rejected,
                ExpectedSha256 = LinuxHash(original), ActualSha256 = actualHash,
                Witness = path, WitnessNeverExecuted = true, FullGame = false
            }));
        }
    }

    /// <summary>
    /// Native Linux guards reject aliases and unsafe filesystem objects before active mutation.
    /// </summary>
    /// <returns>The fixture task.</returns>
    [Test, Platform("Linux")]
    public async Task NativeLinux_GuardsAndAtomicPublicationAsync()
    {
        string root = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_LINUX_FIXTURE_ROOT")! + "-guards";
        var fs = new RealFileSystem();
        Assert.That(fs.Path.Exists(root), Is.False);
        fs.Directory.CreateDirectory(root);
        var engine = new InstallTransactionEngine(fs, _ => long.MaxValue);
        LinuxPut(fs, root, "ordinary.bin", "original");
        Assert.Throws<InvalidDataException>(() => StockArchive.ApplyUnixPermissions(fs, fs.Path.Combine(root, "ordinary.bin"), 0));
        Assert.That(StockArchive.ReadUnixPermissions(fs, fs.Path.Combine(root, "ordinary.bin")), Is.EqualTo(420));
        fs.File.CreateSymbolicLink(fs.Path.Combine(root, "symlink.bin"), fs.Path.Combine(root, "ordinary.bin"));
        Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.Combine(root, "symlink.bin")));
        string hardOriginal = fs.Path.Combine(root, "hard-original.bin");
        LinuxPut(fs, root, "hard-original.bin", "hard-link witness");
        Assert.That(LinuxFixtureLink(hardOriginal, fs.Path.Combine(root, "hard-alias.bin")), Is.Zero);
        Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(hardOriginal));
        LinuxPut(fs, root, "case/Name.bin", "one");
        LinuxPut(fs, root, "case/name.bin", "two");
        Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.Combine(root, "case/name.bin")));
        Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fs.Path.Combine(root, "not-created/../ordinary.bin")));
        string fifo = fs.Path.Combine(root, "fifo");
        Assert.That(LinuxFixtureFifo(fifo, 384), Is.Zero);
        Assert.Throws<InvalidDataException>(() => engine.ValidateControlledPath(fifo));
        LinuxPut(fs, root, "candidate.bin", "candidate");
        LinuxPut(fs, root, "destination.bin", "foreign destination");
        string candidate = fs.Path.Combine(root, "candidate.bin");
        string destination = fs.Path.Combine(root, "destination.bin");
        IOException collision = Assert.Throws<IOException>(() => StockArchive.PublishFile(fs, candidate, destination, false))!;
        Assert.That(((Win32Exception)collision.InnerException!).NativeErrorCode, Is.EqualTo(17));
        Assert.That(fs.File.ReadAllText(candidate), Is.EqualTo("candidate"));
        Assert.That(fs.File.ReadAllText(destination), Is.EqualTo("foreign destination"));
        string otherMount = "/dev/shm/redux-native-launcher-" + Guid.NewGuid().ToString("N");
        Assert.That(fs.Path.Exists(otherMount), Is.False);
        fs.Directory.CreateDirectory(otherMount);
        LinuxPut(fs, otherMount, "candidate.bin", "cross-mount candidate");
        string crossCandidate = fs.Path.Combine(otherMount, "candidate.bin");
        IOException crossMount = Assert.Throws<IOException>(() => StockArchive.PublishFile(fs, crossCandidate,
            fs.Path.Combine(root, "cross-mount.bin"), false))!;
        Assert.That(((Win32Exception)crossMount.InnerException!).NativeErrorCode, Is.EqualTo(18));
        Assert.That(fs.File.ReadAllText(crossCandidate), Is.EqualTo("cross-mount candidate"));
        Assert.That(fs.File.Exists(fs.Path.Combine(root, "cross-mount.bin")), Is.False);
        string game = fs.Path.Combine(root, "game");
        string source = fs.Path.Combine(root, "source");
        var files = new Dictionary<string, string> { ["game.exe"] = "owned stock", ["StreamingAssets/content.bin"] = "owned stock asset" };
        foreach (var file in files)
        {
            LinuxPut(fs, game, file.Key, file.Value);
            LinuxPut(fs, source, file.Key, file.Value);
        }
        StockBaselineManifest stock = LinuxManifest("linux-owned-stock", files);
        string backup = fs.Path.Combine(game, "uninstall.zip");
        var archive = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        await archive.CreateAsync(source, backup, stock);
        byte[] bytes = fs.File.ReadAllBytes(backup);
        var plan = new InstallTransactionPlan(Guid.NewGuid(), game, source, fs.Path.Combine(otherMount, "work"), stock, stock, stock,
            new StockBaselineFile(backup, bytes.Length, LinuxHash(bytes)), useChangedFileStaging: true);
        int mutations = 0;
        var observed = new InstallTransactionEngine(fs, _ => long.MaxValue, (_, _) => mutations++);
        Assert.Throws<InvalidDataException>(() => observed.Apply(plan));
        Assert.That(mutations, Is.Zero);
        Assert.That(fs.File.ReadAllBytes(backup), Is.EqualTo(bytes));
        var changedModes = stock.UnixDirectoryModes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        changedModes["StreamingAssets"] = 504;
        var directoryChange = new StockBaselineManifest(stock.BaselineIdentity, stock.Provenance, stock.Files, stock.Directories, changedModes);
        var metadataPlan = new InstallTransactionPlan(Guid.NewGuid(), game, source, fs.Path.Combine(root, "mode-work"), stock,
            directoryChange, stock, new StockBaselineFile(backup, bytes.Length, LinuxHash(bytes)), useChangedFileStaging: true);
        Assert.Throws<NotSupportedException>(() => observed.Apply(metadataPlan));
        Assert.That(mutations, Is.Zero);
        string extended = fs.Path.Combine(game, "StreamingAssets/content.bin");
        byte[] attribute = Encoding.ASCII.GetBytes("owned metadata witness");
        Assert.That(LinuxFixtureAttribute(extended, "user.omnipatching-fixture", attribute, (nuint)attribute.Length, 0), Is.Zero);
        var extendedPlan = new InstallTransactionPlan(Guid.NewGuid(), game, source, fs.Path.Combine(root, "attribute-work"), stock,
            stock, stock, new StockBaselineFile(backup, bytes.Length, LinuxHash(bytes)), useChangedFileStaging: true);
        Assert.Throws<InvalidDataException>(() => observed.Apply(extendedPlan));
        Assert.That(mutations, Is.Zero);
        fs.File.WriteAllText(fs.Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
        {
            Success = true, NativeLinux = true, SymlinkRejected = true, HardLinkRejected = true,
            UnsupportedPermissionsRejectedBeforeChange = true,
            CaseCollisionRejected = true, TraversalRejected = true, FifoRejectedWithoutBlocking = true,
            ExistingDestinationPreserved = true, AtomicNoReplaceError = 17, CrossMountPublicationError = 18,
            CrossMountTransactionRejectedBeforeMutation = true, ExtendedMetadataRejectedBeforeMutation = true,
            UnsupportedDirectoryModeChangeRejectedBeforeMutation = true, OtherMountRoot = otherMount, FullGame = false
        }));
    }

    /// <summary>
    /// Native post-publication interruption and cancellation restore exact bytes and executable permissions.
    /// </summary>
    /// <param name="cancel">True to cancel after publication, false to inject interruption.</param>
    /// <returns>The fixture task.</returns>
    [TestCase(false), TestCase(true), Platform("Linux")]
    public async Task NativeLinux_PostPublicationRecoveryAsync(bool cancel)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException();
        string root = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_LINUX_FIXTURE_ROOT")! + (cancel ? "-cancel" : "-interrupt");
        var fs = new RealFileSystem();
        Assert.That(fs.Path.Exists(root), Is.False);
        fs.Directory.CreateDirectory(root);
        string game = fs.Path.Combine(root, "game");
        string source = fs.Path.Combine(root, "source");
        string pristine = fs.Path.Combine(root, "stock");
        var oldFiles = new Dictionary<string, string> { ["game.exe"] = "owned stock", ["StreamingAssets/content.bin"] = "owned stock asset" };
        var targetFiles = new Dictionary<string, string> { ["game.exe"] = "owned target", ["StreamingAssets/content.bin"] = "owned rebuilt asset" };
        foreach (var file in oldFiles)
        {
            LinuxPut(fs, game, file.Key, file.Value);
            LinuxPut(fs, pristine, file.Key, file.Value);
        }
        foreach (var file in targetFiles)
        {
            LinuxPut(fs, source, file.Key, file.Value);
        }
        LinuxPut(fs, game, "mods/user.txt", "unknown user bytes");
        var old = LinuxManifest("linux-owned-stock", oldFiles);
        var target = LinuxManifest("linux-owned-target", targetFiles);
        string backup = fs.Path.Combine(game, "uninstall.zip");
        await new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue).CreateAsync(pristine, backup, old);
        byte[] archiveBytes = fs.File.ReadAllBytes(backup);
        var plan = new InstallTransactionPlan(Guid.NewGuid(), game, source, fs.Path.Combine(root, "work"), old, target, old,
            new StockBaselineFile(backup, archiveBytes.Length, LinuxHash(archiveBytes)), useChangedFileStaging: true);
        using var cancellation = new CancellationTokenSource();
        bool published = false;
        var engine = new InstallTransactionEngine(fs, _ => long.MaxValue, (name, after) =>
        {
            if (name != "install-0" || !after)
                return;
            published = true;
            if (cancel)
                cancellation.Cancel();
            else
                throw new IOException("Owned post-publication native Linux interruption.");
        });
        if (cancel)
            Assert.Throws<OperationCanceledException>(() => engine.Apply(plan, cancellation.Token));
        else
            Assert.Throws<IOException>(() => engine.Apply(plan));
        Assert.That(published, Is.True);
        Assert.That(new InstallTransactionEngine(fs, _ => long.MaxValue).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        Assert.That(new InstallTransactionEngine(fs, _ => long.MaxValue).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        foreach (var file in oldFiles)
        {
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(game, file.Key)), Is.EqualTo(file.Value));
        }
        Assert.That((int)fs.File.GetUnixFileMode(fs.Path.Combine(game, "game.exe")), Is.EqualTo(448));
        Assert.That(fs.File.ReadAllBytes(backup), Is.EqualTo(archiveBytes));
        Assert.That(fs.File.ReadAllText(fs.Path.Combine(game, "mods/user.txt")), Is.EqualTo("unknown user bytes"));
        fs.File.WriteAllText(fs.Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            { Success = true, NativeLinux = true, Cancelled = cancel, PublicationObserved = published,
                RepeatedFreshRecoveryRolledBack = true, ExecutablePermissionsRestored = true,
                ArchiveUnchanged = true, UnknownUserBytesPreserved = true, FullGame = false }));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinuxFixtureLink([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int LinuxFixtureFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int LinuxFixtureAttribute([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint length, int flags);
}
