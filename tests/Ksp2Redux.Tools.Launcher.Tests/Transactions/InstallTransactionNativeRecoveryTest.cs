using System.ComponentModel;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

public partial class InstallTransactionEngineTest
{
    private static string LongWindowsPath(string path)
    {
        var buffer = new StringBuilder(32768);
        uint count = GetLongPath(path, buffer, (uint)buffer.Capacity);
        if (count == 0 || count >= buffer.Capacity)
            throw new IOException("Unable to resolve owned Windows temporary path", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }

    /// <summary>
    /// A removed original rolls back by rename even after an interruption in that rollback.
    /// </summary>
    /// <param name="after">Whether recovery stops before or after renaming the original back.</param>
    /// <param name="archiveSource">Whether the target comes from the archive or the ordinary directory stage.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(true, false)]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public async Task NativeMovedUndo_RestartsAfterRemovalWithoutDuplicatingOriginal(bool after, bool archiveSource)
    {
        var (fs, original, root) = NativeFixture();
        try
        {
            string stockRoot = fs.Path.Combine(root, "stock");
            Put(fs, stockRoot, "stock.bin", USER_BYTES);
            StockBaselineManifest stock = new("stock", "owned Windows archive test",
                [new StockBaselineFile("stock.bin", USER_BYTES.Length, Digest(USER_BYTES))]);
            string archive = original.Archive.Path;
            await new StockArchive(fs, new ZipFileService(fs), compressEntries: true).CreateAsync(stockRoot, archive, stock);
            byte[] zip = fs.File.ReadAllBytes(archive);
            string active = fs.Path.Combine(original.InstallRoot, "game.exe");
            byte[] security = WindowsDescriptor(active);
            fs.File.Delete(fs.Path.Combine(original.SourceRoot, "game.exe"));
            StockBaselineManifest target = stock;
            if (!archiveSource)
            {
                Put(fs, original.SourceRoot, "release.bin", USER_BYTES);
                target = new StockBaselineManifest("release", "verified directory stage",
                    [new StockBaselineFile("release.bin", USER_BYTES.Length, Digest(USER_BYTES))]);
            }
            var plan = new InstallTransactionPlan(Guid.NewGuid(), original.InstallRoot, original.SourceRoot, original.TransactionRoot,
                original.Current, target, stock, new StockBaselineFile(archive, zip.Length, Digest(zip)),
                useChangedFileStaging: true, useStockArchiveSource: archiveSource, useMovedFileUndo: true,
                useOwnedSourceStaging: !archiveSource);
            Assert.Throws<IOException>(() => Engine(fs, (name, done) =>
            {
                if (name == "remove-0" && done)
                    throw new IOException("Interrupted after moving the original.");
            }).Apply(plan));
            string removed = fs.Path.Combine(plan.TransactionRoot, "removed", "00000000");
            Assert.That(fs.Directory.EnumerateFileSystemEntries(fs.Path.Combine(plan.TransactionRoot, "undo")), Is.Empty);
            Assert.That(fs.File.Exists(active), Is.False);
            Assert.That(WindowsDescriptor(removed), Is.EqualTo(security));
            fs.File.WriteAllText(active, "old");
            Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
            Assert.That(fs.File.ReadAllText(removed), Is.EqualTo("old"));
            fs.File.Delete(active);
            Assert.Throws<IOException>(() => Engine(fs, (name, done) =>
            {
                if (name == "restore-moved-0" && done == after)
                    throw new IOException("Interrupted at the rollback rename.");
            }).Recover(plan));
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(fs.File.ReadAllText(active), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(active), Is.EqualTo(security));
            Assert.That(fs.File.Exists(removed), Is.False);
            Assert.That(fs.File.ReadAllBytes(archive), Is.EqualTo(zip));
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// An interrupted source move is recovered without a second new-file allocation.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void NativeOwnedStage_RecoversAndReusesItsNewFileWithinSingleCopyBudget()
    {
        var (fs, original, root) = NativeFixture();
        try
        {
            const string NEW_FILE = "release.bin";
            var content = new byte[5 * 1024 * 1024];
            Array.Fill(content, (byte)0x9a);
            Put(fs, original.SourceRoot, NEW_FILE, content);
            var target = new StockBaselineManifest("release", "private owned source",
                [new StockBaselineFile(NEW_FILE, content.Length, Digest(content))]);
            var plan = NewPlan(original.TransactionRoot);
            string source = fs.Path.Combine(plan.SourceRoot, NEW_FILE);
            string staged = fs.Path.Combine(plan.TransactionRoot, "target", "00000000");
            const long SPACE = 70L * 1024 * 1024;
            var interrupted = new InstallTransactionEngine(fs, _ => SPACE, operationBoundary: (name, done) =>
            {
                if (name == "stage-0:move" && done)
                    throw new IOException("Interrupted after the owned source moved.");
            });
            Assert.Throws<IOException>(() => interrupted.Apply(plan));
            Assert.That(fs.File.Exists(source), Is.False);
            Assert.That(fs.FileInfo.New(staged).Length, Is.EqualTo(content.Length));
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(fs.File.Exists(staged), Is.False);
            Assert.That(Digest(fs.File.ReadAllBytes(source)), Is.EqualTo(Digest(content)));
            var retry = NewPlan(fs.Path.Combine(root, "retry"));
            Assert.That(new InstallTransactionEngine(fs, _ => SPACE).Apply(retry), Is.EqualTo(InstallTransactionResult.Committed));
            Assert.That(fs.File.Exists(source), Is.False);
            Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(retry.InstallRoot, NEW_FILE))), Is.EqualTo(Digest(content)));

            InstallTransactionPlan NewPlan(string work) => new(Guid.NewGuid(), original.InstallRoot, original.SourceRoot, work,
                original.Current, target, original.Stock, original.Archive, useChangedFileStaging: true,
                useMovedFileUndo: true, useOwnedSourceStaging: true);
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    private static string ShortWindowsPath(string path)
    {
        var buffer = new StringBuilder(32768);
        uint count = GetShortPath(path, buffer, (uint)buffer.Capacity);
        if (count == 0 || count >= buffer.Capacity)
            throw new IOException("Unable to query owned short path", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static safe extern uint GetLongPath(string path, StringBuilder buffer, uint capacity);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static safe extern uint GetShortPath(string path, StringBuilder buffer, uint capacity);

    private static IFileSystem InterceptReplacement(IFileSystem fs, Action<string, string> replace)
    {
        var files = new Mock<IFile>();
        files.Setup(file => file.GetAttributes(It.IsAny<string>())).Returns((string path) => fs.File.GetAttributes(path));
        files.Setup(file => file.Exists(It.IsAny<string>())).Returns((string path) => fs.File.Exists(path));
        files.Setup(file => file.Delete(It.IsAny<string>())).Callback((string path) => fs.File.Delete(path));
        files.Setup(file => file.Move(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string source, string destination) => fs.File.Move(source, destination));
        files.Setup(file => file.Move(It.IsAny<string>(), It.IsAny<string>(), true))
            .Callback((string source, string destination, bool _) => replace(source, destination));
        files.Setup(file => file.Replace(It.IsAny<string>(), It.IsAny<string>(), null))
            .Callback((string source, string destination, string? _) => replace(source, destination));
        var intercepted = new Mock<IFileSystem>();
        intercepted.SetupGet(fileSystem => fileSystem.File).Returns(files.Object);
        intercepted.SetupGet(fileSystem => fileSystem.Directory).Returns(fs.Directory);
        intercepted.SetupGet(fileSystem => fileSystem.Path).Returns(fs.Path);
        intercepted.SetupGet(fileSystem => fileSystem.FileStream).Returns(fs.FileStream);
        return intercepted.Object;
    }

    /// <summary>
    /// Documented partial native replacement states restore from verified undo and retain every surviving byte.
    /// </summary>
    /// <param name="error">The documented ReplaceFileW error.</param>
    /// <param name="rollback">True to fail a rollback replacement, false to fail apply.</param>
    [TestCase(1175, false)]
    [TestCase(1176, false)]
    [TestCase(1177, false)]
    [TestCase(1175, true)]
    [TestCase(1176, true)]
    [TestCase(1177, true)]
    public void PartialNativeReplacement_RestoresMissingActiveWithVerifiedUndo(int error, bool rollback)
    {
        var (fs, plan) = rollback ? Interrupted() : Fixture();
        string? candidate = null;
        string? activePath = null;
        byte[]? candidateBytes = null;
        byte[]? displacedBytes = null;
        string displaced = fs.Path.Combine(INSTALL, "native-displaced-original.bin");
        IFileSystem intercepted = InterceptReplacement(fs, (source, active) =>
        {
            candidate = source;
            activePath = active;
            candidateBytes = fs.File.ReadAllBytes(source);
            displacedBytes = fs.File.ReadAllBytes(active);
            if (error == 1176)
                fs.File.Delete(active);
            if (error == 1177)
                fs.File.Move(active, displaced);
            throw new IOException("Injected partial ReplaceFileW state", new Win32Exception(error));
        });
        var failing = new InstallTransactionEngine(intercepted, _ => long.MaxValue, validateFileStreams: _ => { });
        Assert.Throws<IOException>(() =>
        {
            if (rollback)
                failing.Recover(plan);
            else
                failing.Apply(plan);
        });
        Assert.That(fs.File.ReadAllBytes(candidate!), Is.EqualTo(candidateBytes));
        if (error != 1175)
            Assert.That(fs.File.Exists(activePath!), Is.False);
        AssertUndo(fs, plan);
        AssertPreserved(fs, plan);
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name.StartsWith("restore-file", StringComparison.Ordinal) && after)
                throw new IOException("Recovery interrupted after restoring a file");
        }).Recover(plan));
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
        AssertUndo(fs, plan);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        if (!rollback)
            Assert.That(fs.File.ReadAllBytes(candidate!), Is.EqualTo(candidateBytes));
        if (error == 1177)
            Assert.That(fs.File.ReadAllBytes(displaced), Is.EqualTo(displacedBytes));
        foreach (var file in plan.Target.Files)
        {
            Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, file.Path.Replace('/', '\\')))), Is.EqualTo(file.Sha256));
        }
    }

    /// <summary>
    /// Backup availability cannot strand valid rollback evidence or silently permit another apply.
    /// </summary>
    /// <param name="kind">The missing, changed, truncated, locked or unreadable archive state.</param>
    [TestCase("missing")]
    [TestCase("changed")]
    [TestCase("truncated")]
    [TestCase("locked")]
    [TestCase("unreadable")]
    public void UnavailableBackup_RecoversWithSeparateHealthAndBlocksFutureApply(string kind)
    {
        var (fs, plan) = Interrupted();
        byte[] journal = fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal"));
        byte[]? retainedArchive = ARCHIVE_BYTES;
        if (kind == "missing")
        {
            fs.File.Delete(plan.Archive.Path);
            retainedArchive = null;
        }
        else if (kind is "changed" or "truncated")
        {
            retainedArchive = kind == "changed" ? ARCHIVE_BYTES.Select(value => (byte)(value ^ 1)).ToArray() : ARCHIVE_BYTES[..7];
            fs.File.WriteAllBytes(plan.Archive.Path, retainedArchive);
        }
        IFileSystem unavailable = fs;
        if (kind is "locked" or "unreadable")
        {
            unavailable = InterceptReplacement(fs, (source, active) => fs.File.Move(source, active, true));
            var streams = new Mock<IFileStreamFactory>();
            streams.Setup(factory => factory.New(It.IsAny<string>(), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>()))
                .Returns((string path, FileMode mode, FileAccess access, FileShare share) =>
                {
                    if (path == plan.Archive.Path)
                    {
                        if (kind == "unreadable")
                            throw new UnauthorizedAccessException("Injected unreadable archive");
                        throw new IOException("Injected sharing violation");
                    }
                    return fs.FileStream.New(path, mode, access, share);
                });
            streams.Setup(factory => factory.New(It.IsAny<string>(), It.IsAny<FileMode>(), It.IsAny<FileAccess>(),
                    It.IsAny<FileShare>(), It.IsAny<int>(), It.IsAny<FileOptions>()))
                .Returns((string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
                    fs.FileStream.New(path, mode, access, share, bufferSize, options));
            Mock.Get(unavailable).SetupGet(fileSystem => fileSystem.FileStream).Returns(streams.Object);
        }
        InstallTransactionEngine Recovering(Action<string, bool>? boundary = null)
            => new(unavailable, _ => long.MaxValue, boundary, _ => { });
        Assert.Throws<IOException>(() => Recovering((name, after) =>
        {
            if (name == "restore-file-0" && after)
                throw new IOException("Interrupted rollback with unavailable backup");
        }).RecoverWithArchiveHealth(plan));
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var result = Recovering().RecoverWithArchiveHealth(plan);
            Assert.That(result.Result, Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(result.ArchiveFailure, Is.Not.Null);
            if (kind == "unreadable")
                Assert.That(result.ArchiveFailure, Is.TypeOf<UnauthorizedAccessException>());
            AssertUndo(fs, plan);
            foreach (var file in plan.Current.Files)
            {
                Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\')))), Is.EqualTo(file.Sha256));
            }
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal")), Is.EqualTo(journal));
            Assert.That(fs.File.Exists(fs.Path.Combine(INSTALL, ".redux-transaction.active")), Is.False);
        }
        IOException? diagnostic = Assert.Throws<IOException>(() => Recovering().Recover(plan));
        Assert.That(diagnostic!.Data["TransactionRecoveryResult"], Is.EqualTo(InstallTransactionResult.RolledBack));
        Assert.That(diagnostic.Data["TransactionArchiveFailure"], Is.InstanceOf<Exception>());
        var next = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, fs.Path.Combine(ROOT, "next-work"),
            plan.Current, plan.Target, plan.Stock, plan.Archive);
        Assert.Catch<Exception>(() => Recovering().Apply(next));
        Assert.That(fs.Directory.Exists(next.TransactionRoot), Is.False);
        Assert.That(fs.File.Exists(fs.Path.Combine(INSTALL, ".redux-transaction.active")), Is.False);
        if (retainedArchive is null)
            Assert.That(fs.File.Exists(plan.Archive.Path), Is.False);
        else
            Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(retainedArchive));
    }

    /// <summary>
    /// A complete commit remains authoritative when archive damage is reported separately.
    /// </summary>
    [Test]
    public void CommittedWithMissingArchive_ReportsHealthWithoutRollingBack()
    {
        var (fs, plan) = Fixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "clear-active" && !after)
                throw new IOException("Interrupted commit marker cleanup");
        }).Apply(plan));
        fs.File.Delete(plan.Archive.Path);
        var recovery = Engine(fs).RecoverWithArchiveHealth(plan);
        Assert.That(recovery.Result, Is.EqualTo(InstallTransactionResult.Committed));
        Assert.That(recovery.ArchiveFailure, Is.InstanceOf<FileNotFoundException>());
        foreach (var file in plan.Target.Files)
        {
            Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\')))), Is.EqualTo(file.Sha256));
        }
        AssertUndo(fs, plan);
        Assert.That(fs.File.Exists(plan.Archive.Path), Is.False);
        Assert.That(Engine(fs).RecoverWithArchiveHealth(plan).Result, Is.EqualTo(InstallTransactionResult.Committed));
    }

    /// <summary>
    /// A missing unchanged file still fails recovery before active mutations.
    /// </summary>
    [Test]
    public void MissingUnchangedFile_IsNotAcceptedAsPartialReplacement()
    {
        var (fs, plan) = Fixture();
        var unchanged = new InstallTransactionPlan(plan.TransactionId, INSTALL, SOURCE, WORK,
            plan.Current, plan.Current, plan.Stock, plan.Archive);
        foreach (var file in plan.Current.Files)
        {
            Put(fs, SOURCE, file.Path, fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\'))));
        }
        fs.Directory.CreateDirectory(fs.Path.Combine(SOURCE, "old-empty"));
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "journal-mutating:flush" && after)
                throw new IOException("Interrupted unchanged generation");
        }).Apply(unchanged));
        fs.File.Delete(fs.Path.Combine(INSTALL, "game.exe"));
        int writes = 0;
        Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => writes++).Recover(unchanged));
        Assert.That(writes, Is.Zero);
        AssertUndo(fs, unchanged);
        AssertPreserved(fs, unchanged);
    }

    /// <summary>
    /// An existing name missing from validated enumeration cannot bypass descendant checks.
    /// </summary>
    [Test]
    public void UnexplainedExistingName_IsRejectedBeforeAnyMutation()
    {
        var (fs, plan) = Fixture();
        IFileSystem intercepted = InterceptReplacement(fs, (source, active) => fs.File.Move(source, active, true));
        var directories = new Mock<IDirectory>();
        directories.Setup(directory => directory.Exists(It.IsAny<string>())).Returns((string path) => fs.Directory.Exists(path));
        directories.Setup(directory => directory.EnumerateFileSystemEntries(It.IsAny<string>()))
            .Returns((string path) => fs.Directory.EnumerateFileSystemEntries(path).Where(entry => entry != INSTALL));
        Mock.Get(intercepted).SetupGet(fileSystem => fileSystem.Directory).Returns(directories.Object);
        int writes = 0;
        var engine = new InstallTransactionEngine(intercepted, _ => long.MaxValue, (_, _) => writes++, _ => { });
        Assert.Throws<InvalidDataException>(() => engine.Apply(plan));
        Assert.That(writes, Is.Zero);
        AssertGeneration(fs, plan, false);
    }

    private static (RealFileSystem Fs, InstallTransactionPlan Plan, string Root) NativeFixture(string relative = "game.exe")
    {
        var fs = new RealFileSystem();
        string root = fs.Path.Combine(LongWindowsPath(fs.Path.GetTempPath()), $"redux-native-recovery-{Guid.NewGuid():N}");
        string install = fs.Path.Combine(root, "Installation with long name");
        string source = fs.Path.Combine(root, "source");
        Put(fs, install, relative, Encoding.UTF8.GetBytes("old"));
        Put(fs, source, relative, Encoding.UTF8.GetBytes("new"));
        Put(fs, install, USER, USER_BYTES);
        Put(fs, install, "uninstall.zip", ARCHIVE_BYTES);
        var old = Manifest("native-old", new Dictionary<string, string> { [relative] = "old" });
        var target = Manifest("native-new", new Dictionary<string, string> { [relative] = "new" });
        var plan = new InstallTransactionPlan(Guid.NewGuid(), install, source, fs.Path.Combine(root, "work"),
            old, target, old, new(fs.Path.Combine(install, "uninstall.zip"), ARCHIVE_BYTES.Length, Digest(ARCHIVE_BYTES)));
        return (fs, plan, root);
    }

    /// <summary>
    /// The Windows engine verifies partial stock staging directly against the pinned archive and can retry.
    /// </summary>
    /// <param name="replacement">Whether the interrupted file is a replacement using a security sibling.</param>
    /// <param name="changeArchive">Whether a changed archive is rejected before recovery deletes the partial data.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Platform("Win")]
    public async Task NativeArchiveBackedStock_RecoversPartialStageAndRetries(bool replacement, bool changeArchive)
    {
        var (fs, original, root) = NativeFixture();
        try
        {
            string stockRoot = fs.Path.Combine(root, "stock");
            string name = replacement ? "game.exe" : "stock.bin";
            var bytes = new byte[2 * 1024 * 1024 + 37];
            Array.Fill(bytes, (byte)0xa5);
            Put(fs, stockRoot, name, bytes);
            var stock = new StockBaselineManifest("stock", "owned Windows archive test",
                [new StockBaselineFile(name, bytes.Length, Digest(bytes))]);
            string archivePath = original.Archive.Path;
            await new StockArchive(fs, new ZipFileService(fs), compressEntries: true).CreateAsync(stockRoot, archivePath, stock);
            byte[] archiveBytes = fs.File.ReadAllBytes(archivePath);
            fs.File.Delete(fs.Path.Combine(original.SourceRoot, "game.exe"));
            var plan = NewPlan(original.TransactionRoot);

            Assert.Throws<IOException>(() => Engine(fs, (boundary, after) =>
            {
                if (boundary == "stage-0:write" && after)
                    throw new IOException("Interrupted the first partial stock stream write.");
            }).Apply(plan));
            string marker = fs.Path.Combine(original.InstallRoot, ".redux-transaction.active");
            Assert.That(fs.File.Exists(marker), Is.True);
            if (changeArchive)
            {
                fs.File.WriteAllBytes(archivePath, [0, 1, 2]);
                Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
                Assert.That(fs.File.Exists(marker), Is.True);
                fs.File.WriteAllBytes(archivePath, archiveBytes);
            }
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(original.InstallRoot, "game.exe")), Is.EqualTo("old"));
            Assert.That(fs.File.Exists(marker), Is.False);

            var retry = NewPlan(fs.Path.Combine(root, "retry"));
            Assert.That(Engine(fs).Apply(retry), Is.EqualTo(InstallTransactionResult.Committed));
            Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(original.InstallRoot, name))), Is.EqualTo(Digest(bytes)));
            Assert.That(fs.Directory.EnumerateFileSystemEntries(original.SourceRoot), Is.Empty);
            Assert.That(fs.File.ReadAllBytes(archivePath), Is.EqualTo(archiveBytes));
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(original.InstallRoot, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));

            InstallTransactionPlan NewPlan(string work) => new(Guid.NewGuid(), original.InstallRoot, original.SourceRoot,
                work, original.Current, stock, stock, new StockBaselineFile(archivePath, archiveBytes.Length, Digest(archiveBytes)),
                useChangedFileStaging: true, useStockArchiveSource: true);
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Real Windows backup locks and missing or foreign bytes do not prevent verified undo recovery.
    /// </summary>
    /// <param name="kind">The archive availability variant.</param>
    [TestCase("locked")]
    [TestCase("missing")]
    [TestCase("changed")]
    [Platform("Win")]
    public void NativeUnavailableBackup_RestoresUndoAndRetainsArchiveState(string kind)
    {
        var (fs, plan, root) = NativeFixture();
        try
        {
            Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
            {
                if (name == "install-0" && after)
                    throw new IOException("Interrupted native publication");
            }).Apply(plan));
            if (kind == "missing")
                fs.File.Delete(plan.Archive.Path);
            if (kind == "changed")
                fs.File.WriteAllBytes(plan.Archive.Path, USER_BYTES);
            using (FileSystemStream? held = kind == "locked" ? fs.FileStream.New(plan.Archive.Path, FileMode.Open,
                       FileAccess.Read, FileShare.None) : null)
            {
                var recovery = Engine(fs).RecoverWithArchiveHealth(plan);
                Assert.That(recovery.Result, Is.EqualTo(InstallTransactionResult.RolledBack));
                Assert.That(recovery.ArchiveFailure, Is.Not.Null);
                Assert.That(fs.File.ReadAllText(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo("old"));
                Assert.That(fs.File.Exists(fs.Path.Combine(plan.InstallRoot, ".redux-transaction.active")), Is.False);
            }
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(plan.TransactionRoot, "undo", "00000000")), Is.EqualTo(Encoding.UTF8.GetBytes("old")));
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(plan.SourceRoot, "game.exe")), Is.EqualTo(Encoding.UTF8.GetBytes("new")));
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));
            if (kind == "missing")
                Assert.That(fs.File.Exists(plan.Archive.Path), Is.False);
            else
                Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(kind == "changed" ? USER_BYTES : ARCHIVE_BYTES));
            TestContext.Progress.WriteLine($"Verified native {kind} archive recovery on {fs.DriveInfo.New(fs.Path.GetPathRoot(root)!).DriveFormat}.");
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Short installation and protected-root aliases cannot bypass stream or overlap checks.
    /// </summary>
    /// <param name="protectedAlias">True for a short protected root, false for a short installation root.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    public void NativeShortAliases_RejectSameRootProtectedOverlap(bool protectedAlias)
    {
        var (fs, plan, root) = NativeFixture();
        try
        {
            string shortInstall = ShortWindowsPath(plan.InstallRoot);
            if (shortInstall == plan.InstallRoot)
                Assert.Ignore("The owned test volume has no short alias for this directory.");
            fs.File.WriteAllBytes(fs.Path.Combine(plan.InstallRoot, "game.exe") + ":metadata", USER_BYTES);
            var alias = new InstallTransactionPlan(plan.TransactionId, protectedAlias ? plan.InstallRoot : shortInstall,
                plan.SourceRoot, plan.TransactionRoot, plan.Current, plan.Target, plan.Stock,
                plan.Archive with { Path = fs.Path.Combine(protectedAlias ? plan.InstallRoot : shortInstall, "uninstall.zip") },
                protectedRoots: [protectedAlias ? shortInstall : plan.InstallRoot]);
            int writes = 0;
            Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => writes++).Apply(alias));
            Assert.That(writes, Is.Zero);
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, "game.exe") + ":metadata"), Is.EqualTo(USER_BYTES));
            AssertGeneration(fs, plan, false);
            TestContext.Progress.WriteLine($"Verified short/long alias rejection for protectedAlias={protectedAlias}.");
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Native stream enumeration and publication support validated paths longer than MAX_PATH.
    /// </summary>
    [Test]
    [Platform("Win")]
    public void NativeLongPaths_RejectDescendantStreamsAndRecoverSingleRename()
    {
        string relative = string.Join('/', Enumerable.Range(0, 4).Select(index => $"{index}-" + new string('a', 65))) + "/game.exe";
        var (fs, plan, root) = NativeFixture(relative);
        try
        {
            string active = fs.Path.Combine(plan.InstallRoot, relative.Replace('/', '\\'));
            Assert.That(active.Length, Is.GreaterThan(260));
            string stream = $@"\\?\{active}:metadata";
            fs.File.WriteAllBytes(stream, USER_BYTES);
            Assert.Throws<InvalidDataException>(() => Engine(fs).Apply(plan));
            Assert.That(fs.File.ReadAllBytes(stream), Is.EqualTo(USER_BYTES));
            fs.File.Delete(stream);
            Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
            {
                if (name == "install-0" && after)
                    throw new IOException("Interrupted native long-path publication");
            }).Apply(plan));
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            AssertGeneration(fs, plan, false);
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(plan.TransactionRoot, "undo", "00000000")), Is.EqualTo(Encoding.UTF8.GetBytes("old")));
            TestContext.Progress.WriteLine($"Verified stream rejection and rename recovery for a {active.Length}-character native path.");
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A descendant short alias cannot bypass named-stream checks.
    /// </summary>
    [Test]
    [Platform("Win")]
    public void NativeDescendantAlias_RetainsNamedStreamsBeforeMutation()
    {
        const string RELATIVE = "Nested directory with long name/game.exe";
        var (fs, plan, root) = NativeFixture(RELATIVE);
        try
        {
            string directory = fs.Path.Combine(plan.InstallRoot, "Nested directory with long name");
            string shortDirectory = ShortWindowsPath(directory);
            string name = fs.Path.GetFileName(shortDirectory);
            if (name == fs.Path.GetFileName(directory))
                Assert.Ignore("The owned test volume has no short alias for this descendant.");
            string active = fs.Path.Combine(directory, "game.exe");
            fs.File.WriteAllBytes(active + ":metadata", USER_BYTES);
            string relative = name + "/game.exe";
            var old = Manifest("alias-old", new Dictionary<string, string> { [relative] = "old" });
            var target = Manifest("alias-new", new Dictionary<string, string> { [relative] = "new" });
            var alias = new InstallTransactionPlan(plan.TransactionId, plan.InstallRoot, plan.SourceRoot,
                plan.TransactionRoot, old, target, plan.Stock, plan.Archive);
            Assert.Throws<InvalidDataException>(() => Engine(fs).Apply(alias));
            Assert.That(fs.File.ReadAllBytes(active + ":metadata"), Is.EqualTo(USER_BYTES));
            AssertGeneration(fs, plan, false);
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Native descendant directory streams and reparse points fail before any owned writes.
    /// </summary>
    /// <param name="reparse">True to use a directory link, false to use a directory stream.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    public void NativeDescendantMetadata_IsRetainedAndRejected(bool reparse)
    {
        const string RELATIVE = "nested/game.exe";
        var (fs, plan, root) = NativeFixture(RELATIVE);
        string directory = fs.Path.Combine(plan.InstallRoot, "nested");
        string outside = fs.Path.Combine(root, "independent-evidence");
        try
        {
            if (reparse)
            {
                Put(fs, outside, "game.exe", Encoding.UTF8.GetBytes("old"));
                fs.Directory.Delete(directory, true);
                try
                {
                    fs.Directory.CreateSymbolicLink(directory, outside);
                }
                catch (UnauthorizedAccessException)
                {
                    Assert.Ignore("Creating an owned directory symbolic link requires local test privileges.");
                }
                catch (IOException failure)
                {
                    if ((failure.HResult & 0xffff) == 1314)
                        Assert.Ignore("Creating an owned directory symbolic link requires local test privileges.");
                    throw;
                }
            }
            else
            {
                fs.File.WriteAllBytes(directory + ":metadata", USER_BYTES);
            }
            int writes = 0;
            Assert.Throws<InvalidDataException>(() => Engine(fs, (_, _) => writes++).Apply(plan));
            Assert.That(writes, Is.Zero);
            AssertGeneration(fs, plan, false);
            if (reparse)
                Assert.That(fs.File.ReadAllText(fs.Path.Combine(outside, "game.exe")), Is.EqualTo("old"));
            else
                Assert.That(fs.File.ReadAllBytes(directory + ":metadata"), Is.EqualTo(USER_BYTES));
        }
        finally
        {
            if (fs.Directory.Exists(directory) && (fs.File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                fs.Directory.Delete(directory);
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// The actual shared Windows publisher refuses cross-volume moves without copying either file.
    /// </summary>
    [Test]
    [Platform("Win")]
    public void NativeCrossVolumePublication_FailsWithoutCopyFallback()
    {
        string? selected = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_CROSS_VOLUME_FIXTURE_ROOT");
        if (string.IsNullOrWhiteSpace(selected))
        {
            Assert.Ignore("Supply an explicitly owned fresh second-volume fixture root through the maintained runner.");
            return;
        }
        var (fs, plan, root) = NativeFixture();
        string? secondRoot = null;
        try
        {
            secondRoot = fs.Path.GetFullPath(selected);
            if (fs.Path.GetPathRoot(secondRoot) == fs.Path.GetPathRoot(root) ||
                !System.Text.RegularExpressions.Regex.IsMatch(fs.Path.GetFileName(secondRoot), "^redux-publication-test-[a-f0-9]{32}$"))
                throw new InvalidDataException("The explicitly owned fixture must be a fresh named root on another volume.");
            Assert.That(fs.Directory.Exists(secondRoot), Is.False);
            // This tests the shared publisher directly, independently of installation-root admission.
            // The explicit fresh fixture may not traverse a filesystem redirect.
            for (string? ancestor = fs.Path.GetDirectoryName(secondRoot); ancestor is not null; ancestor = fs.Path.GetDirectoryName(ancestor))
            {
                if ((fs.File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The owned cross-volume fixture parent redirects elsewhere.");
            }
            fs.Directory.CreateDirectory(secondRoot);
            string candidate = fs.Path.Combine(plan.SourceRoot, "game.exe");
            string destination = fs.Path.Combine(secondRoot, "old.bin");
            fs.File.WriteAllBytes(destination, USER_BYTES);
            IOException? failure = Assert.Throws<IOException>(() => StockArchive.PublishFile(fs, candidate, destination, true));
            var native = (Win32Exception)failure!.InnerException!;
            Assert.That(native.NativeErrorCode, Is.EqualTo(17));
            Assert.That(fs.File.ReadAllBytes(candidate), Is.EqualTo(Encoding.UTF8.GetBytes("new")));
            Assert.That(fs.File.ReadAllBytes(destination), Is.EqualTo(USER_BYTES));
            AssertGeneration(fs, plan, false);
            string receipt = fs.Path.Combine(secondRoot, "result.json");
            fs.File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new
            {
                NativeErrorCode = native.NativeErrorCode, SourceRetained = true, DestinationRetained = true,
                SourceVolume = fs.Path.GetPathRoot(root), DestinationVolume = fs.Path.GetPathRoot(secondRoot),
                CandidateSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs.File.ReadAllBytes(candidate))),
                DestinationSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs.File.ReadAllBytes(destination)))
            }));
            TestContext.AddTestAttachment(receipt, "Actual native cross-volume error and exact retained files.");
            TestContext.Progress.WriteLine("Verified MoveFileExW error 17 on cross-volume publication retains both exact files.");
        }
        finally
        {
            TestContext.Progress.WriteLine($"Owned cross-volume witnesses retained at {root} and {secondRoot}.");
        }
    }
}
