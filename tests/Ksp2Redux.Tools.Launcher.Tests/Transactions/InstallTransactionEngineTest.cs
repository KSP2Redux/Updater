using System.IO.Abstractions;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

/// <summary>
/// Synthetic generation transactions, filesystem failures and restart reconciliation.
/// </summary>
public partial class InstallTransactionEngineTest
{
    private const string ROOT = @"C:\synthetic-transactions";
    private const string INSTALL = ROOT + @"\game";
    private const string SOURCE = ROOT + @"\source";
    private const string WORK = ROOT + @"\transaction";
    private const string USER = "mods/user.bin";
    private static readonly byte[] USER_BYTES = [0, 255, 9, 17, 1];
    private static readonly byte[] ARCHIVE_BYTES = Encoding.UTF8.GetBytes("synthetic previously verified stock archive reference");

    private static (IFileSystem Fs, InstallTransactionPlan Plan) Fixture()
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(INSTALL);
        fs.Directory.CreateDirectory(SOURCE);
        var oldFiles = new Dictionary<string, string>
        {
            ["game.exe"] = "old executable",
            ["StreamingAssets/bundle.bin"] = "old bundle",
            ["obsolete/file.bin"] = "old removed bytes"
        };
        var targetFiles = new Dictionary<string, string>
        {
            ["game.exe"] = "new executable",
            ["StreamingAssets/bundle.bin"] = "new bundle",
            ["added/child/new.bin"] = "new content"
        };
        foreach (var file in oldFiles)
        {
            Put(fs, INSTALL, file.Key, Encoding.UTF8.GetBytes(file.Value));
        }
        foreach (var file in targetFiles)
        {
            Put(fs, SOURCE, file.Key, Encoding.UTF8.GetBytes(file.Value));
        }
        fs.Directory.CreateDirectory(fs.Path.Combine(INSTALL, "old-empty"));
        fs.Directory.CreateDirectory(fs.Path.Combine(SOURCE, "new-empty"));
        Put(fs, INSTALL, USER, USER_BYTES);
        Put(fs, INSTALL, "uninstall.zip", ARCHIVE_BYTES);
        var old = Manifest("stock-1", oldFiles, ["old-empty"]);
        var target = Manifest("release-2", targetFiles, ["new-empty"]);
        var plan = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, WORK, old, target, old,
            new StockBaselineFile(fs.Path.Combine(INSTALL, "uninstall.zip"), ARCHIVE_BYTES.Length, Digest(ARCHIVE_BYTES)));
        return (fs, plan);
    }

    private static StockBaselineManifest Manifest(string identity, Dictionary<string, string> files, string[]? directories = null)
        => new(identity, "synthetic transaction fixture", files.Select(file =>
            new StockBaselineFile(file.Key, Encoding.UTF8.GetByteCount(file.Value), Digest(Encoding.UTF8.GetBytes(file.Value)))), directories);

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Put(IFileSystem fs, string root, string relative, byte[] bytes)
    {
        string path = fs.Path.Combine(root, relative.Replace('/', '\\'));
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
        fs.File.WriteAllBytes(path, bytes);
    }

    private static InstallTransactionEngine Engine(IFileSystem fs, Action<string, bool>? fault = null)
        => new(fs, _ => long.MaxValue, fault, fs is MockFileSystem ? _ => { } : null);

    private static void AssertGeneration(IFileSystem fs, InstallTransactionPlan plan, bool target)
    {
        StockBaselineManifest manifest = target ? plan.Target : plan.Current;
        foreach (StockBaselineFile file in manifest.Files)
        {
            byte[] bytes = fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, file.Path.Replace('/', '\\')));
            Assert.That(bytes.LongLength, Is.EqualTo(file.Length), file.Path);
            Assert.That(Digest(bytes), Is.EqualTo(file.Sha256), file.Path);
        }
        foreach (StockBaselineFile file in (target ? plan.Current : plan.Target).Files)
        {
            if (manifest.Files.Any(kept => kept.Path == file.Path) || plan.RetainedUnownedFiles.Contains(file.Path))
                continue;
            Assert.That(fs.File.Exists(fs.Path.Combine(plan.InstallRoot, file.Path.Replace('/', '\\'))), Is.False, file.Path);
        }
        foreach (string directory in manifest.Directories)
        {
            Assert.That(fs.Directory.Exists(fs.Path.Combine(plan.InstallRoot, directory.Replace('/', '\\'))), Is.True, directory);
        }
        AssertPreserved(fs, plan);
    }

    private static void AssertPreserved(IFileSystem fs, InstallTransactionPlan plan)
    {
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));
        Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(ARCHIVE_BYTES));
    }

    /// <summary>
    /// Apply and terminal recovery verify exact managed bytes without deleting unknown files or stock backup.
    /// </summary>
    [Test]
    public void UpgradeAndCommittedRestart_PreserveEveryExpectedByte()
    {
        var (fs, plan) = Fixture();
        Assert.That(Engine(fs).Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, plan, true);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, plan, true);
        Assert.That(fs.Directory.Exists(fs.Path.Combine(WORK, "undo")), Is.True);
        Assert.That(fs.File.Exists(fs.Path.Combine(INSTALL, ".redux-transaction.active")), Is.False);
    }

    /// <summary>
    /// Uninstall and downgrade use the same ownership mechanism and original immutable archive.
    /// </summary>
    [Test]
    public void UpgradeThenUninstall_RestoresStockWithUnknownFilesIntact()
    {
        var (fs, plan) = Fixture();
        Engine(fs).Apply(plan);
        string stockSource = fs.Path.Combine(ROOT, "stock-source");
        foreach (StockBaselineFile file in plan.Current.Files)
        {
            int index = plan.Current.Files.ToList().IndexOf(file);
            Put(fs, stockSource, file.Path, fs.File.ReadAllBytes(fs.Path.Combine(WORK, "undo", $"{index:D8}")));
        }
        foreach (string directory in plan.Current.Directories)
        {
            fs.Directory.CreateDirectory(fs.Path.Combine(stockSource, directory.Replace('/', '\\')));
        }
        var uninstall = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, stockSource,
            fs.Path.Combine(ROOT, "uninstall-transaction"), plan.Target, plan.Current, plan.Stock, plan.Archive);
        Assert.That(Engine(fs).Apply(uninstall), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, uninstall, true);
        Assert.That(Engine(fs).Recover(uninstall), Is.EqualTo(InstallTransactionResult.Committed));
        AssertGeneration(fs, uninstall, true);
    }

    private static IEnumerable<TestCaseData> ApplyBoundaries()
    {
        var (fs, plan) = Fixture();
        var boundaries = new List<(string Name, bool After)>();
        Engine(fs, (name, after) => boundaries.Add((name, after))).Apply(plan);
        for (int index = 0; index < boundaries.Count; index++)
        {
            yield return new TestCaseData(index, boundaries[index].Name, boundaries[index].After)
                .SetName($"ApplyCrash_{index}_{boundaries[index].Name}_{boundaries[index].After}");
        }
    }

    /// <summary>
    /// Every mutating filesystem boundary is interrupted and recovery restarts in a new engine.
    /// </summary>
    /// <param name="boundary">The exact operation boundary index.</param>
    /// <param name="name">The operation name.</param>
    /// <param name="after">True for interruption after the operation, false before it.</param>
    [TestCaseSource(nameof(ApplyBoundaries))]
    public void CrashAtEveryApplyBoundary_RestartsOrRetainsUnambiguousEvidence(int boundary, string name, bool after)
    {
        var (fs, plan) = Fixture();
        int observed = 0;
        Assert.Throws<IOException>(() => Engine(fs, (operation, completed) =>
        {
            if (observed++ == boundary)
            {
                Assert.That(operation, Is.EqualTo(name));
                Assert.That(completed, Is.EqualTo(after));
                throw new IOException("synthetic process termination");
            }
        }).Apply(plan));
        AssertPreserved(fs, plan);
        InstallTransactionResult result = Engine(fs).Recover(plan);
        AssertGeneration(fs, plan, result == InstallTransactionResult.Committed);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(result));
        AssertGeneration(fs, plan, result == InstallTransactionResult.Committed);
    }

    private static (IFileSystem Fs, InstallTransactionPlan Plan) Interrupted()
    {
        var (fs, plan) = Fixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "install-2" && after)
                throw new IOException("synthetic crash after replacement");
        }).Apply(plan));
        return (fs, plan);
    }

    private static IEnumerable<TestCaseData> RollbackBoundaries()
    {
        var (fs, plan) = Interrupted();
        var boundaries = new List<(string Name, bool After)>();
        Engine(fs, (name, after) => boundaries.Add((name, after))).Recover(plan);
        for (int index = 0; index < boundaries.Count; index++)
        {
            yield return new TestCaseData(index).SetName($"RollbackCrash_{index}_{boundaries[index].Name}_{boundaries[index].After}");
        }
    }

    /// <summary>
    /// Rollback interruption retains undo and a second restart restores every original managed byte.
    /// </summary>
    /// <param name="boundary">The rollback operation boundary index.</param>
    [TestCaseSource(nameof(RollbackBoundaries))]
    public void CrashAtEveryRollbackBoundary_IsIdempotent(int boundary)
    {
        var (fs, plan) = Interrupted();
        int observed = 0;
        Assert.Throws<IOException>(() => Engine(fs, (_, _) =>
        {
            if (observed++ == boundary)
                throw new IOException("synthetic rollback interruption");
        }).Recover(plan));
        AssertPreserved(fs, plan);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// External edits stop rollback before it changes any other managed file.
    /// </summary>
    [Test]
    public void ExternalChange_RetainsJournalUndoAndUnknownBytes()
    {
        var (fs, plan) = Interrupted();
        byte[] external = [33, 77, 22, 99];
        Put(fs, INSTALL, "game.exe", external);
        byte[] bundle = fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "StreamingAssets", "bundle.bin"));
        byte[] journal = fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal"));
        Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "game.exe")), Is.EqualTo(external));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "StreamingAssets", "bundle.bin")), Is.EqualTo(bundle));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(WORK, "journal")), Is.EqualTo(journal));
        AssertPreserved(fs, plan);
    }

    /// <summary>
    /// Unexplained edits and oversized journals fail closed before active bytes change.
    /// </summary>
    /// <param name="kind">The journal corruption variant.</param>
    [TestCase("foreign-tail")]
    [TestCase("hash")]
    [TestCase("oversized")]
    [TestCase("version")]
    public void CorruptJournal_RetainsEvidence(string kind)
    {
        var (fs, plan) = Interrupted();
        string path = fs.Path.Combine(WORK, "journal");
        byte[] bytes = fs.File.ReadAllBytes(path);
        bytes = kind switch
        {
            "foreign-tail" => [.. bytes, (byte)'X'],
            "hash" => bytes.Select((value, index) => index == 10 ? (byte)'Z' : value).ToArray(),
            "version" => bytes.Select((value, index) => index == 0 ? (byte)'2' : value).ToArray(),
            _ => new byte[4097]
        };
        fs.File.WriteAllBytes(path, bytes);
        byte[] active = fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "game.exe"));
        Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
        Assert.That(fs.File.ReadAllBytes(path), Is.EqualTo(bytes));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "game.exe")), Is.EqualTo(active));
        AssertPreserved(fs, plan);
    }

    /// <summary>
    /// Unknown target collisions fail unless their exact-byte unowned policy is recorded.
    /// </summary>
    [Test]
    public void ExactUnownedCollision_RequiresExplicitPolicyAndSurvivesRollback()
    {
        var (fs, plan) = Fixture();
        string relative = "added/child/new.bin";
        Put(fs, INSTALL, relative, fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, relative.Replace('/', '\\'))));
        Assert.Throws<InvalidDataException>(() => Engine(fs).Apply(plan));
        var retained = new InstallTransactionPlan(plan.TransactionId, INSTALL, SOURCE, WORK, plan.Current,
            plan.Target, plan.Stock, plan.Archive, [relative], ["added", "added/child"]);
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "install-0" && after)
                throw new IOException("synthetic interruption");
        }).Apply(retained));
        Assert.That(Engine(fs).Recover(retained), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, retained, false);
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, relative.Replace('/', '\\'))),
            Is.EqualTo(fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, relative.Replace('/', '\\')))));
    }

    /// <summary>
    /// Wrong staged bytes, wrong backup and unknown disk space stop before touching managed content.
    /// </summary>
    /// <param name="failure">The preparation failure.</param>
    [TestCase("staging")]
    [TestCase("archive")]
    [TestCase("space")]
    public void PreparationFailure_DoesNotMutateActiveGeneration(string failure)
    {
        var (fs, plan) = Fixture();
        if (failure == "staging")
            Put(fs, SOURCE, "game.exe", [255]);
        if (failure == "archive")
            fs.File.WriteAllBytes(plan.Archive.Path, [9]);
        var engine = failure == "space" ? new InstallTransactionEngine(fs, _ => null, validateFileStreams: _ => { }) : Engine(fs);
        Assert.That(() => engine.Apply(plan), Throws.Exception);
        foreach (StockBaselineFile file in plan.Current.Files)
        {
            Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\')))), Is.EqualTo(file.Sha256));
        }
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));
        if (failure == "staging")
        {
            Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
            Assert.That(fs.Directory.Exists(WORK), Is.True);
        }
    }

    /// <summary>
    /// Cancellation cleans preparation or restores the previous generation after mutation.
    /// </summary>
    /// <param name="operation">The cancellation boundary.</param>
    [TestCase("stage-0:write")]
    [TestCase("install-1")]
    public void Cancellation_CleansOrRollsBack(string operation)
    {
        var (fs, plan) = Fixture();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Engine(fs, (name, after) =>
        {
            if (name == operation && after)
                cancellation.Cancel();
        }).Apply(plan, cancellation.Token));
        AssertGeneration(fs, plan, false);
        Assert.That(fs.File.Exists(fs.Path.Combine(INSTALL, ".redux-transaction.active")), Is.False);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Active markers reject a different work root and a foreign recovery plan.
    /// </summary>
    [Test]
    public void ActiveAndForeignTransactions_AreRejected()
    {
        var (fs, plan) = Interrupted();
        var foreign = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, fs.Path.Combine(ROOT, "other"),
            plan.Current, plan.Target, plan.Stock, plan.Archive);
        Assert.Throws<IOException>(() => Engine(fs).Apply(foreign));
        Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(foreign));
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Unknown children prevent directory deletion during update and rollback.
    /// </summary>
    [Test]
    public void UnknownChildrenInRemovedDirectories_AreNeverDeleted()
    {
        var (fs, plan) = Fixture();
        byte[] unknown = [8, 6, 4, 2];
        Put(fs, INSTALL, "obsolete/user.bin", unknown);
        Engine(fs).Apply(plan);
        AssertGeneration(fs, plan, true);
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, "obsolete", "user.bin")), Is.EqualTo(unknown));
    }

    /// <summary>
    /// A real contained filesystem exercises Windows replacement, sharing locks and restart recovery.
    /// </summary>
    [Test]
    [Platform("Win")]
    public void RealFilesystem_SingleRenameAndExclusiveLockUseContainedTemporaryRoot()
    {
        var fs = new RealFileSystem();
        string root = fs.Path.Combine(LongWindowsPath(fs.Path.GetTempPath()), $"redux-transaction-test-{Guid.NewGuid():N}");
        string install = fs.Path.Combine(root, "game");
        string source = fs.Path.Combine(root, "source");
        fs.Directory.CreateDirectory(install);
        fs.Directory.CreateDirectory(source);
        try
        {
            Put(fs, install, "game.exe", Encoding.UTF8.GetBytes("old"));
            Put(fs, source, "game.exe", Encoding.UTF8.GetBytes("new"));
            Put(fs, install, USER, USER_BYTES);
            Put(fs, install, "uninstall.zip", ARCHIVE_BYTES);
            var old = Manifest("real-old", new Dictionary<string, string> { ["game.exe"] = "old" });
            var target = Manifest("real-new", new Dictionary<string, string> { ["game.exe"] = "new" });
            var plan = new InstallTransactionPlan(Guid.NewGuid(), install, source, fs.Path.Combine(root, "work"),
                old, target, old, new StockBaselineFile(fs.Path.Combine(install, "uninstall.zip"), ARCHIVE_BYTES.Length, Digest(ARCHIVE_BYTES)));
            using (FileSystemStream locked = fs.FileStream.New(fs.Path.Combine(install, ".redux-transaction.lock"),
                       FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => Engine(fs).Apply(plan));
            }
            using (FileSystemStream locked = fs.FileStream.New(fs.Path.Combine(install, "game.exe"),
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => Engine(fs).Apply(plan));
            }
            string namedStream = fs.Path.Combine(install, "game.exe") + ":user-metadata";
            fs.File.WriteAllBytes(namedStream, USER_BYTES);
            Assert.Throws<InvalidDataException>(() => Engine(fs).Apply(plan));
            Assert.That(fs.File.ReadAllBytes(namedStream), Is.EqualTo(USER_BYTES));
            AssertGeneration(fs, plan, false);
            fs.File.Delete(namedStream);
            Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
            {
                if (name == "install-0" && after)
                    throw new IOException("synthetic real filesystem crash");
            }).Apply(plan));
            Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            AssertGeneration(fs, plan, false);
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            fs.Directory.Delete(root, true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] WindowsDescriptor(string path)
        => FileSystemAclExtensions.GetAccessControl(new FileInfo(path),
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();

    [SupportedOSPlatform("windows")]
    private static void PreserveWindowsSecurityFailure(RealFileSystem fs, string root, byte[] original)
    {
        string[] files = fs.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var observations = files.Select(path =>
        {
            byte[] descriptor = WindowsDescriptor(path);
            var parsed = new RawSecurityDescriptor(descriptor, 0);
            return new
            {
                Path = fs.Path.GetRelativePath(root, path),
                BytesSha256 = Digest(fs.File.ReadAllBytes(path)),
                RawDescriptorBase64 = Convert.ToBase64String(descriptor),
                DescriptorSha256 = Digest(descriptor),
                ControlFlags = (int)parsed.ControlFlags,
                OwnerSha256 = Digest(Encoding.UTF8.GetBytes(parsed.Owner!.Value)),
                GroupSha256 = Digest(Encoding.UTF8.GetBytes(parsed.Group!.Value)),
                OrderedAces = parsed.DiscretionaryAcl!.Cast<GenericAce>().Select(ace =>
                {
                    byte[] binary = new byte[ace.BinaryLength];
                    ace.GetBinaryForm(binary, 0);
                    return new { Type = ace.AceType.ToString(), Flags = ace.AceFlags.ToString(), Sha256 = Digest(binary) };
                }).ToArray()
            };
        }).ToArray();
        fs.File.WriteAllText(fs.Path.Combine(root, "windows-security-observation.json"),
            JsonSerializer.Serialize(new { OriginalDescriptorBase64 = Convert.ToBase64String(original), Files = observations }));
        TestContext.Progress.WriteLine($"Windows security failure retained at {root}");
    }

    [SupportedOSPlatform("windows")]
    private static (RealFileSystem Fs, InstallTransactionPlan Plan, string Root, byte[] OriginalSecurity) WindowsSecurityFixture(bool protectedOriginal = false)
    {
        var fs = new RealFileSystem();
        string root = fs.Path.Combine(LongWindowsPath(fs.Path.GetTempPath()), $"redux-transaction-security-{Guid.NewGuid():N}");
        string install = fs.Path.Combine(root, "game");
        string source = fs.Path.Combine(root, "source");
        string privateParent = fs.Path.Combine(root, "private");
        fs.Directory.CreateDirectory(install);
        fs.Directory.CreateDirectory(source);
        var privateSecurity = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root));
        privateSecurity.SetAccessRuleProtection(true, true);
        privateSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null),
            FileSystemRights.Delete, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        // Different inheritance is supplied only while creating this new owned test directory.
        FileSystemAclExtensions.Create(new DirectoryInfo(privateParent), privateSecurity);
        if (protectedOriginal)
        {
            var originalSecurity = new FileSecurity();
            originalSecurity.SetSecurityDescriptorBinaryForm(FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(install),
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
            originalSecurity.SetAccessRuleProtection(true, true);
            using FileStream output = FileSystemAclExtensions.Create(new FileInfo(fs.Path.Combine(install, "game.exe")),
                FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadPermissions, FileShare.None, 4096,
                FileOptions.None, originalSecurity);
            output.Write(Encoding.UTF8.GetBytes("old"));
            output.Flush(true);
        }
        else
            Put(fs, install, "game.exe", Encoding.UTF8.GetBytes("old"));
        Put(fs, source, "game.exe", Encoding.UTF8.GetBytes("new"));
        Put(fs, source, "new.bin", Encoding.UTF8.GetBytes("added"));
        Put(fs, privateParent, "context.bin", Encoding.UTF8.GetBytes("context"));
        Put(fs, install, "uninstall.zip", ARCHIVE_BYTES);
        byte[] original = WindowsDescriptor(fs.Path.Combine(install, "game.exe"));
        Assert.That(WindowsDescriptor(fs.Path.Combine(privateParent, "context.bin")), Is.Not.EqualTo(original),
            "The actual inherited staging and installation descriptors must differ.");
        var old = Manifest("security-old", new Dictionary<string, string> { ["game.exe"] = "old" });
        var target = Manifest("security-new", new Dictionary<string, string> { ["game.exe"] = "new", ["new.bin"] = "added" });
        var plan = new InstallTransactionPlan(Guid.NewGuid(), install, source, fs.Path.Combine(privateParent, "work"),
            old, target, old, new StockBaselineFile(fs.Path.Combine(install, "uninstall.zip"), ARCHIVE_BYTES.Length, Digest(ARCHIVE_BYTES)),
            useChangedFileStaging: true);
        return (fs, plan, root, original);
    }

    /// <summary>
    /// Reserve room for one destination candidate and release its stage before continuing.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsPublication_ReservesSpaceForOneNewFileAndReleasesItsStage()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool preserve = false;
        try
        {
            Put(fs, plan.InstallRoot, USER, USER_BYTES);
            long newFileBytes = plan.Target.Files.Single(file => file.Path == "new.bin").Length;
            long stagingSpace = 64L * 1024 * 1024 + 2L * plan.ToJson().Length +
                plan.Current.TotalBytes + plan.Target.TotalBytes + newFileBytes;
            var insufficient = new InstallTransactionEngine(fs, _ => stagingSpace - 1);

            Assert.That(() => insufficient.Apply(plan),
                Throws.TypeOf<IOException>().With.Message.StartsWith("Insufficient or unknown transaction disk space."));
            Assert.That(fs.Directory.Exists(plan.TransactionRoot), Is.False);
            AssertGeneration(fs, plan, false);

            var sufficient = new InstallTransactionEngine(fs, _ => stagingSpace);
            Assert.That(sufficient.Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
            AssertGeneration(fs, plan, true);
            Assert.That(fs.Directory.EnumerateFileSystemEntries(fs.Path.Combine(plan.TransactionRoot, "target")), Is.Empty);
        }
        catch
        {
            preserve = true;
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            if (!preserve)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Replacement and undo retain the original descriptor while new content inherits its destination parent.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsReplacementAndUndo_PreserveOriginalSecurityAcrossDifferentStagingInheritance()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool preserve = false;
        try
        {
            Assert.That(new InstallTransactionEngine(fs).Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.TransactionRoot, "undo", "00000000")), Is.EqualTo(original));
            Assert.That(Encoding.UTF8.GetString(fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, "game.exe"))), Is.EqualTo("new"));
            Put(fs, plan.InstallRoot, "inheritance-witness.bin", Encoding.UTF8.GetBytes("witness"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "new.bin")),
                Is.EqualTo(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "inheritance-witness.bin"))));
            new InstallTransactionEngine(fs).RetireTerminalPayloads(plan, InstallTransactionResult.Committed,
                plan.Target, new string('A', 64));
            Assert.That(fs.Directory.Exists(fs.Path.Combine(plan.TransactionRoot, "undo")), Is.False);
            Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(ARCHIVE_BYTES));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            TestContext.Out.WriteLine($"Committed and retired descriptor SHA256 {Digest(original)}");
        }
        catch
        {
            preserve = true;
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            if (!preserve)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Restart recovery restores bytes and security after publication or an interrupted destination candidate copy.
    /// </summary>
    /// <param name="operation">The actual engine boundary interrupted after its operation.</param>
    [TestCase("install-0")]
    [TestCase("windows-candidate-1:write")]
    [TestCase("install-1:publish")]
    [TestCase("install-1:release-stage")]
    [TestCase("write-windows-publication-authority:write")]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsInterruptedPublication_RecoversOriginalSecurity(string operation)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool injected = false;
        try
        {
            var engine = new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == operation && after)
                {
                    injected = true;
                    throw new IOException("Interrupted owned Windows publication.");
                }
            });
            Assert.Throws<IOException>(() => engine.Apply(plan));
            Assert.That(injected, Is.True);
            if (operation is "install-1:publish" or "install-1:release-stage")
                Assert.That(fs.File.Exists(fs.Path.Combine(plan.TransactionRoot, "target", "00000001")),
                    Is.EqualTo(operation == "install-1:publish"));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(Encoding.UTF8.GetString(fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, "game.exe"))), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            Assert.That(fs.Directory.EnumerateFiles(plan.InstallRoot, $".redux-transaction-{plan.TransactionId:N}-*.tmp"), Is.Empty);
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            fs.Directory.Delete(root, true);
        }
    }

    private static IEnumerable<TestCaseData> WindowsSiblingBoundaries()
    {
        foreach (string role in new[] { "stage-0", "undo-0", "rollback-copy-0" })
        foreach (string operation in new[] { "open", "write", "flush", "security", "transfer" })
        foreach (bool after in new[] { false, true })
            yield return new TestCaseData(role, operation, after);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] WindowsParentDescriptor(string path)
        => FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path),
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();

    /// <summary>
    /// Every new sibling copy boundary supports independent repeated restart with original bytes and security.
    /// </summary>
    /// <param name="role">The staged target, undo or rollback copy.</param>
    /// <param name="operation">The actual copy or qualification operation.</param>
    /// <param name="after">True to interrupt after the operation, false before it.</param>
    [TestCaseSource(nameof(WindowsSiblingBoundaries))]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingCopies_InterruptedBoundariesRecover(string role, string operation, bool after)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        byte[] parent = WindowsParentDescriptor(plan.InstallRoot);
        byte[] privateParent = WindowsParentDescriptor(fs.Path.GetDirectoryName(plan.TransactionRoot)!);
        bool passed = false;
        try
        {
            if (role == "rollback-copy-0")
                Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, completed) =>
                {
                    if (name == "install-0" && completed)
                        throw new IOException("Begin real rollback fixture.");
                }).Apply(plan));
            bool injected = false;
            var interrupted = new InstallTransactionEngine(fs, operationBoundary: (name, completed) =>
            {
                if (name != $"{role}:{operation}" || completed != after)
                    return;
                injected = true;
                throw new IOException("Interrupt owned sibling operation.");
            });
            Assert.Throws<IOException>(() =>
            {
                if (role == "rollback-copy-0")
                    interrupted.Recover(plan);
                else
                    interrupted.Apply(plan);
            });
            Assert.That(injected, Is.True);
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            Assert.That(WindowsParentDescriptor(plan.InstallRoot), Is.EqualTo(parent));
            Assert.That(WindowsParentDescriptor(fs.Path.GetDirectoryName(plan.TransactionRoot)!), Is.EqualTo(privateParent));
            Assert.That(fs.Directory.EnumerateFiles(plan.InstallRoot, $".redux-transaction-{plan.TransactionId:N}-*.tmp"), Is.Empty);
            TestContext.Out.WriteLine($"Recovered {role}:{operation}:{after} descriptor SHA256 {Digest(original)}");
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Interrupted owned-sibling cleanup restarts without changing active bytes before complete recovery validation.
    /// </summary>
    /// <param name="after">True to interrupt after deletion, false before it.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingCleanup_InterruptedDeletionRecovers(bool after)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool passed = false;
        try
        {
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, completed) =>
            {
                if (name == "install-0" && completed)
                    throw new IOException("Begin cleanup fixture.");
            }).Apply(plan));
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, completed) =>
            {
                if (name == "rollback-copy-0:flush" && completed)
                    throw new IOException("Retain owned rollback sibling.");
            }).Recover(plan));
            bool injected = false;
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, completed) =>
            {
                if (name == "cleanup-windows-security-0" && completed == after)
                {
                    injected = true;
                    throw new IOException("Interrupt verified sibling cleanup.");
                }
            }).Recover(plan));
            Assert.That(injected, Is.True);
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo("new"));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Cleanup rechecks a sibling changed after the initial recovery validation and retains foreign content.
    /// </summary>
    /// <param name="preparing">True for preparation cleanup, false for rollback cleanup.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingCleanup_RechecksContentBeforeDeletion(bool preparing)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool passed = false;
        try
        {
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == (preparing ? "stage-0:flush" : "install-0") && after)
                    throw new IOException("Retain copy for cleanup recheck.");
            }).Apply(plan));
            if (!preparing)
                Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
                {
                    if (name == "rollback-copy-0:flush" && after)
                        throw new IOException("Retain rollback sibling.");
                }).Recover(plan));
            string candidate = fs.Path.Combine(plan.InstallRoot, $".redux-transaction-{plan.TransactionId:N}-security-00000000.tmp");
            bool injected = false;
            Assert.Throws<InvalidDataException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == "cleanup-windows-security-0" && !after)
                {
                    fs.File.WriteAllText(candidate, "foreign content after validation");
                    injected = true;
                }
            }).Recover(plan));
            Assert.That(injected, Is.True);
            Assert.That(fs.File.ReadAllText(candidate), Is.EqualTo("foreign content after validation"));
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(preparing ? "old" : "new"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Protected originals keep their actual complete descriptor without needless inheritance mutation.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingCopies_ProtectedOriginalPreservesSecurity()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture(protectedOriginal: true);
        bool passed = false;
        try
        {
            Assert.That(new InstallTransactionEngine(fs).Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.TransactionRoot, "undo", "00000000")), Is.EqualTo(original));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.Committed));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Occupied, corrupt and legacy sibling paths never grant deletion authority from a name alone.
    /// </summary>
    /// <param name="kind">The ownership conflict or legacy marker policy.</param>
    [TestCase("occupied")]
    [TestCase("corrupt")]
    [TestCase("partial-marker")]
    [TestCase("conflicting-markers")]
    [TestCase("legacy-v1")]
    [TestCase("legacy-unmarked")]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingOwnership_ConflictsRetainUnknownContent(string kind)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool passed = false;
        try
        {
            string candidate = fs.Path.Combine(plan.InstallRoot, $".redux-transaction-{plan.TransactionId:N}-security-00000000.tmp");
            if (kind is "corrupt" or "partial-marker")
            {
                string boundary = kind == "corrupt" ? "stage-0:flush" : "write-windows-publication-authority:write";
                bool injected = false;
                Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
                {
                    if (name == boundary && after)
                    {
                        injected = true;
                        throw new IOException("Retain preparation evidence for ownership conflict.");
                    }
                }).Apply(plan));
                Assert.That(injected, Is.True);
            }
            else if (kind != "occupied")
                Assert.That(new InstallTransactionEngine(fs).Apply(plan), Is.EqualTo(InstallTransactionResult.Committed));
            string v2 = fs.Path.Combine(plan.TransactionRoot, "windows-publication-v2");
            if (kind == "partial-marker")
                fs.File.WriteAllBytes(v2, new[] { fs.File.ReadAllBytes(v2)[0] });
            if (kind is "legacy-v1" or "legacy-unmarked")
                fs.File.Delete(v2);
            if (kind is "legacy-v1" or "conflicting-markers")
                fs.File.WriteAllText(fs.Path.Combine(plan.TransactionRoot, "windows-publication-v1"),
                    $"windows-publication-v1\n{plan.GetDigest()}\n", new UTF8Encoding(false));
            fs.File.WriteAllText(candidate, "unrecognized sibling bytes");
            if (kind == "occupied")
                Assert.Throws<InvalidDataException>(() => new InstallTransactionEngine(fs).Apply(plan));
            else if (kind.StartsWith("legacy-", StringComparison.Ordinal))
                Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.Committed));
            else
                Assert.Throws<InvalidDataException>(() => new InstallTransactionEngine(fs).Recover(plan));
            Assert.That(fs.File.ReadAllText(candidate), Is.EqualTo("unrecognized sibling bytes"));
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(plan.InstallRoot, "game.exe")),
                Is.EqualTo(kind is "occupied" or "corrupt" or "partial-marker" ? "old" : "new"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A managed target cannot occupy a finite security sibling reserved for a different existing file.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingOwnership_ManagedCollisionRefusesBeforeIntent()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool passed = false;
        try
        {
            string collision = $".redux-transaction-{plan.TransactionId:N}-security-00000000.tmp";
            var target = Manifest("sibling-collision", new Dictionary<string, string> { ["game.exe"] = "new", [collision] = "managed" });
            Put(fs, plan.SourceRoot, collision, Encoding.UTF8.GetBytes("managed"));
            Assert.Throws<InvalidDataException>(() =>
            {
                plan = new InstallTransactionPlan(plan.TransactionId, plan.InstallRoot, plan.SourceRoot, plan.TransactionRoot,
                    plan.Current, target, plan.Stock, plan.Archive, useChangedFileStaging: true);
                new InstallTransactionEngine(fs).Apply(plan);
            });
            Assert.That(fs.File.Exists(fs.Path.Combine(plan.InstallRoot, ".redux-transaction.active")), Is.False);
            Assert.That(fs.Directory.Exists(plan.TransactionRoot), Is.False);
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Completed preparation aborts do not reacquire a recreated sibling even when its bytes match the old file.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingAbort_RecreatedSiblingRefusesRepeatedRecovery()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool passed = false;
        try
        {
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == "stage-0:flush" && after)
                    throw new IOException("Abort owned preparation.");
            }).Apply(plan));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            string candidate = fs.Path.Combine(plan.InstallRoot, $".redux-transaction-{plan.TransactionId:N}-security-00000000.tmp");
            fs.File.WriteAllText(candidate, "old");
            Assert.Throws<InvalidDataException>(() => new InstallTransactionEngine(fs).Recover(plan));
            Assert.That(fs.File.ReadAllText(candidate), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Aborted preparation can retire disposable payloads without claiming undo that was never published.
    /// </summary>
    /// <param name="partialMarker">True for an interrupted version marker, false for an interrupted file copy.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsSiblingAbort_RetirementPreservesOriginal(bool partialMarker)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool passed = false;
        try
        {
            string boundary = partialMarker ? "write-windows-publication-authority:write" : "stage-0:flush";
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == boundary && after)
                    throw new IOException("Abort before prepared state.");
            }).Apply(plan));
            if (partialMarker)
            {
                string marker = fs.Path.Combine(plan.TransactionRoot, "windows-publication-v2");
                fs.File.WriteAllBytes(marker, new[] { fs.File.ReadAllBytes(marker)[0] });
            }
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            var engine = new InstallTransactionEngine(fs);
            engine.RetireTerminalPayloads(plan, InstallTransactionResult.RolledBack, plan.Target, new string('A', 64));
            engine.RetireTerminalPayloads(plan, InstallTransactionResult.RolledBack, plan.Target, new string('A', 64));
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
            Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(ARCHIVE_BYTES));
            passed = true;
        }
        catch
        {
            PreserveWindowsSecurityFailure(fs, root, original);
            throw;
        }
        finally
        {
            if (passed)
                fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A candidate with correct bytes and changed security is rejected before replacing the active file.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsCandidateSecurityDrift_RefusesBeforeActivePublication()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        bool driftInjected = false;
        try
        {
            var engine = new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name != "install-0:publish" || after)
                    return;
                string candidate = fs.Path.Combine(plan.TransactionRoot, "target", "00000000");
                string different = fs.Path.Combine(fs.Path.GetDirectoryName(plan.TransactionRoot)!, "different.bin");
                fs.File.WriteAllBytes(different, Encoding.UTF8.GetBytes("new"));
                fs.File.Move(different, candidate, true);
                driftInjected = true;
            });
            Assert.Throws<InvalidDataException>(() => engine.Apply(plan));
            Assert.That(driftInjected, Is.True, "The actual publication boundary must be reached before drift rejection qualifies.");
            Assert.That(Encoding.UTF8.GetString(fs.File.ReadAllBytes(fs.Path.Combine(plan.InstallRoot, "game.exe"))), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(fs.Path.Combine(plan.InstallRoot, "game.exe")), Is.EqualTo(original));
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Interrupted rollback repairs old bytes with the wrong descriptor using genuine undo for replacement and deletion.
    /// </summary>
    /// <param name="deletion">True for planned deletion, false for replacement.</param>
    [TestCase(false)]
    [TestCase(true)]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsInterruptedRollback_OldBytesWithWrongSecurityRequireRepair(bool deletion)
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        try
        {
            if (deletion)
            {
                var target = Manifest("security-deletion", new Dictionary<string, string> { ["new.bin"] = "added" });
                plan = new InstallTransactionPlan(plan.TransactionId, plan.InstallRoot, plan.SourceRoot, plan.TransactionRoot,
                    plan.Current, target, plan.Stock, plan.Archive, useChangedFileStaging: true);
            }
            string interruptedApply = deletion ? "remove-0" : "install-0";
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == interruptedApply && after)
                    throw new IOException("Interrupted before rollback.");
            }).Apply(plan));
            string active = fs.Path.Combine(plan.InstallRoot, "game.exe");
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name != "restore-file-0" || !after)
                    return;
                string different = fs.Path.Combine(fs.Path.GetDirectoryName(plan.TransactionRoot)!, "interrupted-old.bin");
                fs.File.WriteAllBytes(different, Encoding.UTF8.GetBytes("old"));
                fs.File.Move(different, active, true);
                throw new IOException("Interrupted rollback after old bytes but before terminal verification.");
            }).Recover(plan));
            Assert.That(Encoding.UTF8.GetString(fs.File.ReadAllBytes(active)), Is.EqualTo("old"));
            Assert.That(WindowsDescriptor(active), Is.Not.EqualTo(original));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            Assert.That(WindowsDescriptor(active), Is.EqualTo(original));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A terminal rollback refuses same-byte descriptor drift instead of reporting success from byte checks.
    /// </summary>
    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void WindowsTerminalRollback_WrongSecurityCannotReportRolledBack()
    {
        var (fs, plan, root, original) = WindowsSecurityFixture();
        try
        {
            Assert.Throws<IOException>(() => new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
            {
                if (name == "install-0" && after)
                    throw new IOException("Interrupted before rollback.");
            }).Apply(plan));
            Assert.That(new InstallTransactionEngine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
            string active = fs.Path.Combine(plan.InstallRoot, "game.exe");
            string different = fs.Path.Combine(fs.Path.GetDirectoryName(plan.TransactionRoot)!, "terminal-old.bin");
            fs.File.WriteAllBytes(different, Encoding.UTF8.GetBytes("old"));
            fs.File.Move(different, active, true);
            Assert.That(WindowsDescriptor(active), Is.Not.EqualTo(original));
            Assert.Throws<InvalidDataException>(() => new InstallTransactionEngine(fs).Recover(plan));
            Assert.That(Encoding.UTF8.GetString(fs.File.ReadAllBytes(active)), Is.EqualTo("old"));
        }
        finally
        {
            Assert.That(root, Does.StartWith(LongWindowsPath(fs.Path.GetTempPath())));
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Windows path aliases and file/directory transitions are rejected without active changes.
    /// </summary>
    /// <param name="path">The invalid target relative path.</param>
    [TestCase("../escape")]
    [TestCase("file:stream")]
    [TestCase("NUL.txt")]
    [TestCase("COM1")]
    [TestCase("trailing.")]
    [TestCase("trailing ")]
    [TestCase("/root")]
    [TestCase("GAME.exe")]
    [TestCase("game.exe/child")]
    [TestCase("uninstall.zip")]
    [TestCase(".redux-transaction.active")]
    public void UnsafeRelativePaths_RejectContracts(string path)
    {
        var (fs, plan) = Fixture();
        Assert.Throws<InvalidDataException>(() =>
        {
            var target = Manifest("invalid", new Dictionary<string, string> { [path] = "bytes" });
            _ = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, WORK, plan.Current, target, plan.Stock, plan.Archive);
        });
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Conflicting, traversing and protected disk roots fail before staging or managed mutation.
    /// </summary>
    /// <param name="kind">The unsafe root contract.</param>
    [TestCase("install")]
    [TestCase("source")]
    [TestCase("protected-install")]
    [TestCase("protected-work")]
    [TestCase("archive")]
    [TestCase("relative")]
    [TestCase("traversal")]
    [TestCase("device")]
    [TestCase("unc")]
    [TestCase("volume")]
    [TestCase("other-volume")]
    public void UnsafeRoots_RejectWithoutActiveChanges(string kind)
    {
        var (fs, plan) = Fixture();
        string work = kind switch
        {
            "install" => fs.Path.Combine(INSTALL, "work"),
            "source" => fs.Path.Combine(SOURCE, "work"),
            "archive" => plan.Archive.Path,
            "relative" => "relative-work",
            "traversal" => ROOT + @"\source\..\work",
            "device" => @"\\?\C:\synthetic-transactions\work",
            "unc" => @"\\server\share\work",
            "volume" => @"C:\",
            "other-volume" => @"D:\work",
            _ => WORK
        };
        string[] protectedRoots = kind switch
        {
            "protected-install" => [INSTALL],
            "protected-work" => [ROOT],
            _ => []
        };
        var invalid = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, work, plan.Current,
            plan.Target, plan.Stock, plan.Archive, protectedRoots: protectedRoots);
        Assert.That(() => Engine(fs).Apply(invalid), Throws.Exception);
        AssertGeneration(fs, plan, false);
        Assert.That(fs.Directory.Exists(WORK), Is.False);
    }

    /// <summary>
    /// Reparse points in source, active ancestors, work and archive paths are rejected.
    /// </summary>
    /// <param name="location">The existing path marked as a reparse point.</param>
    [TestCase("source")]
    [TestCase("active-ancestor")]
    [TestCase("archive")]
    [TestCase("work-parent")]
    public void ReparsePaths_RejectWithoutFollowingThem(string location)
    {
        var (fs, plan) = Fixture();
        string path = location switch
        {
            "source" => SOURCE,
            "active-ancestor" => fs.Path.Combine(INSTALL, "StreamingAssets"),
            "archive" => plan.Archive.Path,
            _ => ROOT
        };
        string linkTarget = @"C:\synthetic-link-target";
        if (location == "archive")
        {
            fs.File.Move(path, linkTarget);
            fs.File.CreateSymbolicLink(path, linkTarget);
        }
        else
        {
            fs.Directory.Move(path, linkTarget);
            fs.Directory.CreateSymbolicLink(path, linkTarget);
        }
        Assert.Throws<InvalidDataException>(() => Engine(fs).Apply(plan));
        if (location == "archive")
        {
            fs.File.Delete(path);
            fs.File.Move(linkTarget, path);
        }
        else
        {
            fs.Directory.Delete(path);
            fs.Directory.Move(linkTarget, path);
        }
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Corrupt plan bytes and corrupt undo bytes stop restart before any active mutation.
    /// </summary>
    /// <param name="kind">The damaged evidence file.</param>
    [TestCase("plan")]
    [TestCase("undo")]
    [TestCase("marker")]
    public void CorruptRecoveryEvidence_IsNeverTrusted(string kind)
    {
        var (fs, plan) = Interrupted();
        string path = kind switch
        {
            "plan" => fs.Path.Combine(WORK, "plan.json"),
            "undo" => fs.Path.Combine(WORK, "undo", "00000000"),
            _ => fs.Path.Combine(INSTALL, ".redux-transaction.active")
        };
        fs.File.WriteAllBytes(path, [10, 20, 30]);
        byte[][] active = plan.Target.Files.Select(file => fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\')))).ToArray();
        Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
        for (int index = 0; index < plan.Target.Files.Count; index++)
        {
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, plan.Target.Files[index].Path.Replace('/', '\\'))), Is.EqualTo(active[index]));
        }
        Assert.That(fs.File.ReadAllBytes(path), Is.EqualTo(new byte[] { 10, 20, 30 }));
        AssertPreserved(fs, plan);
    }

    /// <summary>
    /// Cancellation reports a retained resumable transaction when rollback itself fails.
    /// </summary>
    [Test]
    public void CancelledRollbackFailure_IsExplicitAndCanRestart()
    {
        var (fs, plan) = Fixture();
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException? failure = Assert.Throws<OperationCanceledException>(() => Engine(fs, (name, after) =>
        {
            if (name == "install-1" && after)
                cancellation.Cancel();
            if (name.StartsWith("restore-file", StringComparison.Ordinal) && !after)
                throw new IOException("synthetic locked rollback destination");
        }).Apply(plan, cancellation.Token));
        Assert.That(failure!.Data["TransactionRecoveryFailure"], Is.InstanceOf<IOException>());
        Assert.That(fs.File.Exists(fs.Path.Combine(INSTALL, ".redux-transaction.active")), Is.True);
        AssertPreserved(fs, plan);
        Assert.That(Engine(fs).Recover(plan), Is.EqualTo(InstallTransactionResult.RolledBack));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// Repair requires an explicit actual-current contract, preserving damaged bytes for rollback.
    /// </summary>
    [Test]
    public void RepairWithExplicitCurrentContract_StagesExactTargetAndRetainsDamagedUndo()
    {
        var (fs, plan) = Fixture();
        byte[] damaged = Encoding.UTF8.GetBytes("known damaged current executable");
        Put(fs, INSTALL, "game.exe", damaged);
        Assert.Throws<InvalidDataException>(() => Engine(fs).Apply(plan));
        var actualCurrent = new StockBaselineManifest("damaged-generation", "explicit caller repair contract",
            plan.Current.Files.Select(file => file.Path == "game.exe"
                ? new StockBaselineFile(file.Path, damaged.Length, Digest(damaged)) : file), plan.Current.Directories);
        var repair = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, WORK, actualCurrent,
            plan.Target, plan.Stock, plan.Archive);
        Engine(fs).Apply(repair);
        AssertGeneration(fs, repair, true);
        int index = actualCurrent.Files.ToList().FindIndex(file => file.Path == "game.exe");
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(WORK, "undo", $"{index:D8}")), Is.EqualTo(damaged));
    }

    /// <summary>
    /// Empty unowned parent directories remain unowned across later uninstall plans.
    /// </summary>
    [Test]
    public void UnownedDirectoryContainingManagedFiles_SurvivesWhenLastManagedFileIsRemoved()
    {
        var (fs, plan) = Fixture();
        string directory = "StreamingAssets";
        var target = new StockBaselineManifest("removed-bundle", "synthetic uninstall", plan.Current.Files.Where(file => !file.Path.StartsWith(directory, StringComparison.Ordinal)), ["old-empty"]);
        string source = fs.Path.Combine(ROOT, "retained-source");
        foreach (StockBaselineFile file in target.Files)
        {
            Put(fs, source, file.Path, fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\'))));
        }
        fs.Directory.CreateDirectory(fs.Path.Combine(source, "old-empty"));
        var removal = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, source, WORK, plan.Current,
            target, plan.Stock, plan.Archive, existingUnownedDirectories: [directory]);
        Engine(fs).Apply(removal);
        AssertGeneration(fs, removal, true);
        Assert.That(fs.Directory.Exists(fs.Path.Combine(INSTALL, directory)), Is.True);
    }

    /// <summary>
    /// Unknown preparation files prevent cleanup and remain available for diagnosis.
    /// </summary>
    [Test]
    public void UnknownPreparationBytes_PreventCleanup()
    {
        var (fs, plan) = Fixture();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "stage-0:flush" && after)
                throw new IOException("synthetic preparation failure");
        }).Apply(plan));
        string unknown = fs.Path.Combine(WORK, "target", "unknown-user-file");
        fs.File.WriteAllBytes(unknown, USER_BYTES);
        Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
        Assert.That(fs.File.ReadAllBytes(unknown), Is.EqualTo(USER_BYTES));
        AssertGeneration(fs, plan, false);
    }

    /// <summary>
    /// The shared archive primitive supplies a verified stock target to the same uninstall engine.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task VerifiedStockArchive_IntegratesWithUpgradeAndUninstallOnSyntheticTrees()
    {
        var (fs, plan) = Fixture();
        var stockArchive = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        await stockArchive.CreateAsync(INSTALL, plan.Archive.Path, plan.Stock);
        await stockArchive.VerifyAsync(plan.Archive.Path, plan.Stock);
        byte[] archiveBytes = fs.File.ReadAllBytes(plan.Archive.Path);
        var archiveReference = new StockBaselineFile(plan.Archive.Path, archiveBytes.Length, Digest(archiveBytes));
        var install = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, SOURCE, WORK,
            plan.Current, plan.Target, plan.Stock, archiveReference);
        Engine(fs).Apply(install);
        Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(archiveBytes));
        string restored = fs.Path.Combine(ROOT, "verified-restored-stock");
        await stockArchive.RestoreToNewDirectoryAsync(plan.Archive.Path, plan.Stock, restored);
        var uninstall = new InstallTransactionPlan(Guid.NewGuid(), INSTALL, restored,
            fs.Path.Combine(ROOT, "verified-uninstall-work"), plan.Target, plan.Stock,
            plan.Stock, archiveReference, protectedRoots: [WORK]);
        Engine(fs).Apply(uninstall);
        Assert.That(Engine(fs).Recover(uninstall), Is.EqualTo(InstallTransactionResult.Committed));
        foreach (StockBaselineFile file in plan.Stock.Files)
        {
            Assert.That(Digest(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\')))), Is.EqualTo(file.Sha256));
        }
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, USER.Replace('/', '\\'))), Is.EqualTo(USER_BYTES));
        Assert.That(fs.File.ReadAllBytes(plan.Archive.Path), Is.EqualTo(archiveBytes));
        await stockArchive.VerifyAsync(plan.Archive.Path, plan.Stock);
    }

    /// <summary>
    /// Candidate bytes that cannot originate from an interrupted undo copy remain as evidence.
    /// </summary>
    [Test]
    public void ExternallyCorruptedRollbackCandidate_IsRetainedBeforeActiveMutation()
    {
        var (fs, plan) = Interrupted();
        Assert.Throws<IOException>(() => Engine(fs, (name, after) =>
        {
            if (name == "rollback-copy-0:write" && after)
                throw new IOException("synthetic interrupted undo copy");
        }).Recover(plan));
        string candidate = fs.Path.Combine(WORK, "rollback", "00000000");
        fs.File.WriteAllBytes(candidate, USER_BYTES);
        byte[][] active = plan.Target.Files.Select(file => fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, file.Path.Replace('/', '\\')))).ToArray();
        Assert.Throws<InvalidDataException>(() => Engine(fs).Recover(plan));
        for (int index = 0; index < plan.Target.Files.Count; index++)
        {
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(INSTALL, plan.Target.Files[index].Path.Replace('/', '\\'))), Is.EqualTo(active[index]));
        }
        Assert.That(fs.File.ReadAllBytes(candidate), Is.EqualTo(USER_BYTES));
        AssertPreserved(fs, plan);
    }
}
