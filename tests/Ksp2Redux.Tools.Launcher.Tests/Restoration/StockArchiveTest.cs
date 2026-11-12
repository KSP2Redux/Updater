using System.Buffers.Binary;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Wrappers;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Restoration;

/// <summary>
/// Synthetic stock archive contracts, byte verification and failure recovery.
/// </summary>
public class StockArchiveTest
{
    private const string ROOT = @"C:\synthetic";
    private const string SOURCE = @"C:\synthetic\source";
    private const string ARCHIVE = @"C:\synthetic\uninstall.zip";
    private const string RESTORED = @"C:\synthetic\restored";
    private const string BUNDLE = "KSP2_x64_Data/StreamingAssets/aa/stock.bundle";

    private static (MockFileSystem Fs, StockBaselineManifest Manifest, StockArchive Service) Fixture(
        StockArchiveLimits? limits = null, Func<string, long?>? space = null)
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(SOURCE);
        var files = new Dictionary<string, byte[]>
        {
            ["KSP2_x64.exe"] = [1, 2, 3, 4],
            [BUNDLE] = Encoding.UTF8.GetBytes("synthetic stock bundle bytes"),
            ["KSP2_x64_Data/StreamingAssets/empty.bin"] = []
        };
        foreach (var file in files)
        {
            string path = fs.Path.Combine(SOURCE, file.Key.Replace('/', '\\'));
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
            fs.File.WriteAllBytes(path, file.Value);
        }
        fs.Directory.CreateDirectory(fs.Path.Combine(SOURCE, "empty-directory"));
        fs.File.WriteAllText(fs.Path.Combine(SOURCE, "user-save.json"), "preserve user bytes");
        var manifest = new StockBaselineManifest("synthetic/depot/build-1", "disposable test contract",
            files.Select(file => new StockBaselineFile(file.Key, file.Value.Length, Digest(file.Value))),
            ["empty-directory"]);
        return (fs, manifest, new StockArchive(fs, new ZipFileService(fs), limits, space ?? (_ => long.MaxValue)));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Zip(StockBaselineManifest manifest, IEnumerable<(string Name, byte[] Bytes, int Attributes)> entries,
        byte[]? metadata = null, bool includeMetadata = true, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            if (includeMetadata)
            {
                using Stream content = zip.CreateEntry(StockBaselineManifest.MANIFEST_PATH, CompressionLevel.NoCompression).Open();
                content.Write(metadata ?? manifest.ToJson());
            }
            foreach (var item in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(item.Name, compression);
                entry.ExternalAttributes = item.Attributes;
                using Stream content = entry.Open();
                content.Write(item.Bytes);
            }
        }
        return stream.ToArray();
    }

    private static IEnumerable<(string Name, byte[] Bytes, int Attributes)> Entries(MockFileSystem fs, StockBaselineManifest manifest)
        => manifest.Directories.Select(directory => (directory + "/", Array.Empty<byte>(), 0))
            .Concat(manifest.Files.Select(file => (file.Path,
                fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, file.Path.Replace('/', '\\'))), 0)));

    /// <summary>
    /// Full content includes StreamingAssets and empty paths, with unknown user files preserved.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task FullArchive_RestoresExactExpectedTreeAndLeavesUserFilesAlone()
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        await service.VerifyAsync(ARCHIVE, manifest);
        await service.RestoreToNewDirectoryAsync(ARCHIVE, manifest, RESTORED);

        string[] actual = fs.Directory.EnumerateFiles(RESTORED, "*", SearchOption.AllDirectories)
            .Select(path => fs.Path.GetRelativePath(RESTORED, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.That(actual, Is.EqualTo(manifest.Files.Select(file => file.Path)));
        foreach (StockBaselineFile file in manifest.Files)
        {
            byte[] bytes = fs.File.ReadAllBytes(fs.Path.Combine(RESTORED, file.Path.Replace('/', '\\')));
            Assert.That(Digest(bytes), Is.EqualTo(file.Sha256), file.Path);
        }
        string[] directories = fs.Directory.EnumerateDirectories(RESTORED, "*", SearchOption.AllDirectories)
            .Select(path => fs.Path.GetRelativePath(RESTORED, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.That(directories, Is.EqualTo(manifest.Directories));
        Assert.That(fs.File.ReadAllText(fs.Path.Combine(SOURCE, "user-save.json")), Is.EqualTo("preserve user bytes"));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
    }

    /// <summary>
    /// Verify that compressed creation can use less free space than the complete stock file payload.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task CompressedArchive_FitsBoundedSpaceAndRestoresExactStock()
    {
        var (fs, _, reader) = Fixture();
        string source = fs.Path.Combine(SOURCE, BUNDLE.Replace('/', '\\'));
        byte[] stock = Enumerable.Repeat((byte)0x5a, 4 * 1024 * 1024).ToArray();
        fs.File.WriteAllBytes(source, stock);
        var manifest = new StockBaselineManifest("compressed-stock", "disposable compression fixture",
            [new StockBaselineFile(BUNDLE, stock.Length, Digest(stock))], ["empty-directory"]);
        const int reserve = 4096;
        const int archiveBudget = 65536;
        var limits = new StockArchiveLimits { DiskReserveBytes = reserve, MaxArchiveBytes = archiveBudget };
        var writer = new StockArchive(fs, new ZipFileService(fs), limits, _ => archiveBudget + reserve, compressEntries: true);

        await writer.CreateAsync(SOURCE, ARCHIVE, manifest);
        await reader.VerifyAsync(ARCHIVE, manifest);
        await reader.RestoreToNewDirectoryAsync(ARCHIVE, manifest, RESTORED);

        Assert.That(fs.FileInfo.New(ARCHIVE).Length, Is.LessThan(archiveBudget));
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(RESTORED, BUNDLE.Replace('/', '\\'))), Is.EqualTo(stock));
        Assert.That(fs.Directory.Exists(fs.Path.Combine(RESTORED, "empty-directory")), Is.True);
    }

    /// <summary>
    /// Preserve an existing backup if compressed output exhausts the staging budget.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task CompressedArchive_ExhaustedBudgetKeepsExistingBackup()
    {
        var (fs, previous, reader) = Fixture();
        await reader.CreateAsync(SOURCE, ARCHIVE, previous);
        byte[] original = fs.File.ReadAllBytes(ARCHIVE);
        string source = fs.Path.Combine(SOURCE, BUNDLE.Replace('/', '\\'));
        var random = new byte[256 * 1024];
        new Random(537).NextBytes(random);
        fs.File.WriteAllBytes(source, random);
        var manifest = new StockBaselineManifest("incompressible-stock", "disposable compression fixture",
            [new StockBaselineFile(BUNDLE, random.Length, Digest(random))]);
        var writer = new StockArchive(fs, new ZipFileService(fs), new StockArchiveLimits { DiskReserveBytes = 4096 },
            _ => 32768, compressEntries: true);

        await Assert.ThrowsAsync<IOException>(async () => await writer.CreateAsync(SOURCE, ARCHIVE, manifest));

        Assert.That(fs.File.ReadAllBytes(ARCHIVE), Is.EqualTo(original));
        Assert.That(fs.File.ReadAllBytes(source), Is.EqualTo(random));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
        await reader.VerifyAsync(ARCHIVE, previous);
    }

    /// <summary>
    /// The self-contained manifest round trips without inferring authentication.
    /// </summary>
    [Test]
    public void Manifest_RoundTripsIdentityProvenanceAndCanonicalPaths()
    {
        var (_, manifest, _) = Fixture();
        StockBaselineManifest parsed = StockBaselineManifest.FromJson(manifest.ToJson());
        Assert.That(parsed.ToJson(), Is.EqualTo(manifest.ToJson()));
        var normalized = new StockBaselineManifest("local scan", "not authenticated",
            [new StockBaselineFile(@"data\a.bin", 0, Digest([]))]);
        Assert.That(normalized.Files[0].Path, Is.EqualTo("data/a.bin"));
        Assert.That(normalized.Provenance, Is.EqualTo("not authenticated"));
    }

    /// <summary>
    /// Windows-invalid stock paths are rejected independently of the host platform.
    /// </summary>
    /// <param name="path">The malicious relative path.</param>
    [TestCase("../escape")]
    [TestCase("/rooted")]
    [TestCase(@"C:\absolute")]
    [TestCase(@"\\server\share")]
    [TestCase(@"\\?\C:\device")]
    [TestCase("file:stream")]
    [TestCase("a//b")]
    [TestCase("a/./b")]
    [TestCase("a/../b")]
    [TestCase("NUL.txt")]
    [TestCase("con")]
    [TestCase("COM1.cfg")]
    [TestCase("LPT9")]
    [TestCase("COM\u00b9.txt")]
    [TestCase("CONIN$")]
    [TestCase("file.")]
    [TestCase("file ")]
    [TestCase("a\u0001b")]
    [TestCase("a*b")]
    [TestCase(".REDUX-STOCK/payload")]
    public void Manifest_RejectsUnsafeWindowsPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => new StockBaselineManifest("test", "synthetic",
            [new StockBaselineFile(path, 0, Digest([]))]));
    }

    /// <summary>
    /// File duplicates, directory conflicts and differently cased parent paths are rejected.
    /// </summary>
    /// <param name="first">The first file path.</param>
    /// <param name="second">The conflicting file path.</param>
    [TestCase("a", "a")]
    [TestCase("A", "a")]
    [TestCase("a", "a/b")]
    [TestCase("a/b", "a")]
    [TestCase("Data/a", "data/b")]
    public void Manifest_RejectsCaseAndFileDirectoryConflicts(string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => new StockBaselineManifest("test", "synthetic",
            [new StockBaselineFile(first, 0, Digest([])), new StockBaselineFile(second, 0, Digest([]))]));
    }

    /// <summary>
    /// Invalid entry bytes, lengths and archive truncation cannot produce a restored tree.
    /// </summary>
    /// <param name="kind">The corruption kind.</param>
    /// <returns>The test task.</returns>
    [TestCase("hash")]
    [TestCase("length")]
    [TestCase("truncate")]
    public async Task CorruptArchive_IsRejectedBeforeRestore(string kind)
    {
        var (fs, manifest, service) = Fixture();
        var entries = Entries(fs, manifest).ToList();
        if (kind == "hash")
        {
            int index = entries.FindIndex(entry => entry.Name == BUNDLE);
            entries[index].Bytes[0] ^= 0x7F;
        }
        else if (kind == "length")
        {
            int index = entries.FindIndex(entry => entry.Name == BUNDLE);
            entries[index] = (BUNDLE, [1], 0);
        }
        byte[] bytes = Zip(manifest, entries);
        fs.File.WriteAllBytes(ARCHIVE, kind == "truncate" ? bytes[..^17] : bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.VerifyAsync(ARCHIVE, manifest));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.RestoreToNewDirectoryAsync(ARCHIVE, manifest, RESTORED));
        Assert.That(fs.Directory.Exists(RESTORED), Is.False);
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
    }

    /// <summary>
    /// Entry attacks fail even when the central-directory entry count matches the contract.
    /// </summary>
    /// <param name="name">The replacement entry name.</param>
    /// <param name="attributes">The replacement platform attributes.</param>
    /// <returns>The test task.</returns>
    [TestCase("../escape", 0)]
    [TestCase("/escape", 0)]
    [TestCase("KSP2_x64.exe:stream", 0)]
    [TestCase("KSP2_X64.EXE", 0)]
    [TestCase(@"KSP2_x64_Data\StreamingAssets\aa\stock.bundle", 0)]
    [TestCase("KSP2_x64.exe", 0)]
    [TestCase(BUNDLE, unchecked((int)0xA0000000))]
    [TestCase(BUNDLE, (int)FileAttributes.ReparsePoint)]
    public async Task MaliciousEntry_IsRejected(string name, int attributes)
    {
        var (fs, manifest, service) = Fixture();
        var entries = Entries(fs, manifest).ToList();
        int index = entries.FindIndex(entry => entry.Name == BUNDLE);
        entries[index] = (name, entries[index].Bytes, attributes);
        fs.File.WriteAllBytes(ARCHIVE, Zip(manifest, entries));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.VerifyAsync(ARCHIVE, manifest));
        Assert.That(fs.File.Exists(@"C:\escape"), Is.False);
    }

    /// <summary>
    /// A partial legacy archive is never accepted as the complete versioned contract.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task LegacyArchiveWithoutMetadata_IsRejected()
    {
        var (fs, manifest, service) = Fixture();
        fs.File.WriteAllBytes(ARCHIVE, Zip(manifest, [("KSP2_x64.exe", [1, 2, 3, 4], 0)], includeMetadata: false));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.VerifyAsync(ARCHIVE, manifest));
    }

    /// <summary>
    /// An archive manifest cannot choose its own identity, provenance or expected hashes.
    /// </summary>
    /// <param name="field">The changed contract field.</param>
    /// <returns>The test task.</returns>
    [TestCase("identity")]
    [TestCase("provenance")]
    [TestCase("hash")]
    [TestCase("version")]
    public async Task DifferentManifestContract_IsRejected(string field)
    {
        var (fs, manifest, service) = Fixture();
        string json = Encoding.UTF8.GetString(manifest.ToJson());
        json = field switch
        {
            "identity" => json.Replace("build-1", "build-2", StringComparison.Ordinal),
            "provenance" => json.Replace("disposable", "authoritative", StringComparison.Ordinal),
            "hash" => json.Replace(manifest.Files[0].Sha256, new string('0', 64), StringComparison.Ordinal),
            _ => json.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal)
        };
        fs.File.WriteAllBytes(ARCHIVE, Zip(manifest, Entries(fs, manifest), Encoding.UTF8.GetBytes(json)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.VerifyAsync(ARCHIVE, manifest));
    }

    /// <summary>
    /// Input and disk faults retain a previously verified archive and remove owned temp files.
    /// </summary>
    /// <param name="fault">The fault to inject.</param>
    /// <returns>The test task.</returns>
    [TestCase("wrong-bytes")]
    [TestCase("missing")]
    [TestCase("space")]
    [TestCase("unknown-space")]
    [TestCase("write-failure")]
    [TestCase("cancelled")]
    public async Task CreationFailure_PreservesExistingValidArchive(string fault)
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        byte[] oldArchive = fs.File.ReadAllBytes(ARCHIVE);
        var cancellation = new CancellationTokenSource();
        switch (fault)
        {
            case "wrong-bytes":
                fs.File.WriteAllBytes(fs.Path.Combine(SOURCE, "KSP2_x64.exe"), [9, 8, 7, 6]);
                break;
            case "missing":
                fs.File.Delete(fs.Path.Combine(SOURCE, "KSP2_x64.exe"));
                break;
            case "space":
                service = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => 0);
                break;
            case "unknown-space":
                service = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => null);
                break;
            case "write-failure":
            case "cancelled":
                var zip = new ZipFileService(fs);
                var factory = new Mock<IZipFileService>();
                factory.Setup(candidate => candidate.NewArchive(It.IsAny<Stream>(), ZipArchiveMode.Create, true))
                    .Returns((Stream stream, ZipArchiveMode mode, bool leaveOpen) =>
                    {
                        stream.WriteByte(42);
                        if (fault == "cancelled")
                        {
                            cancellation.Cancel();
                            throw new OperationCanceledException(cancellation.Token);
                        }
                        throw new IOException("Injected disk-full write failure");
                    });
                factory.Setup(candidate => candidate.NewArchive(It.IsAny<Stream>(), ZipArchiveMode.Read, true))
                    .Returns((Stream stream, ZipArchiveMode mode, bool leaveOpen) => zip.NewArchive(stream, mode, leaveOpen));
                service = new StockArchive(fs, factory.Object, availableSpace: _ => long.MaxValue);
                break;
        }
        Assert.That(async () => await service.CreateAsync(SOURCE, ARCHIVE, manifest, cancellation.Token), Throws.Exception);
        Assert.That(fs.File.ReadAllBytes(ARCHIVE), Is.EqualTo(oldArchive));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
        await new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue).VerifyAsync(ARCHIVE, manifest);
        cancellation.Dispose();
    }

    /// <summary>
    /// Successful replacement atomically publishes a verified archive without losing the source.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task SuccessfulReplacement_RemainsFullyVerified()
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        await service.VerifyAsync(ARCHIVE, manifest);
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
    }

    /// <summary>
    /// Restore never overwrites an existing tree, including an empty one.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task RestoreExistingTree_IsRejectedAndPreservesUnknownFiles()
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        fs.Directory.CreateDirectory(RESTORED);
        fs.File.WriteAllText(fs.Path.Combine(RESTORED, "save.json"), "keep");
        Assert.That(async () => await service.RestoreToNewDirectoryAsync(ARCHIVE, manifest, RESTORED), Throws.Exception);
        Assert.That(fs.File.ReadAllText(fs.Path.Combine(RESTORED, "save.json")), Is.EqualTo("keep"));
    }

    /// <summary>
    /// Stream verification preserves caller ownership and position on success and failure.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task CallerStream_RemainsOpenAndPositionedAfterVerification()
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        using var input = new MemoryStream(fs.File.ReadAllBytes(ARCHIVE));
        input.Position = 7;
        await service.VerifyAsync(input, manifest);
        Assert.That(input.Position, Is.EqualTo(7));
        Assert.That(input.CanRead, Is.True);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.VerifyAsync(input, manifest, cancelled.Token));
        Assert.That(input.Position, Is.EqualTo(7));
        Assert.That(input.CanRead, Is.True);
        var wrong = new StockBaselineManifest("wrong", "wrong", manifest.Files, manifest.Directories);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.VerifyAsync(input, wrong));
        Assert.That(input.Position, Is.EqualTo(7));
        Assert.That(input.CanRead, Is.True);
    }

    /// <summary>
    /// Consumed disk paths reject reparse points in files and ancestors before archive mutation.
    /// </summary>
    /// <param name="kind">The path to mark as a reparse point.</param>
    /// <returns>The test task.</returns>
    [TestCase("file")]
    [TestCase("source")]
    [TestCase("destination")]
    public async Task ReparseDiskPath_IsRejected(string kind)
    {
        var (fs, manifest, service) = Fixture();
        string path = kind switch
        {
            "file" => fs.Path.Combine(SOURCE, "KSP2_x64.exe"),
            "source" => SOURCE,
            _ => ROOT
        };
        string target = @"C:\synthetic-link-target";
        if (kind == "file")
        {
            fs.File.Move(path, target);
            fs.File.CreateSymbolicLink(path, target);
        }
        else
        {
            fs.Directory.Move(path, target);
            fs.Directory.CreateSymbolicLink(path, target);
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.CreateAsync(SOURCE, ARCHIVE, manifest));
        Assert.That(fs.File.Exists(ARCHIVE), Is.False);
    }

    /// <summary>
    /// Real filesystem locking fails cleanly without replacing a valid archive.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task LockedRealInput_PreservesExistingArchive()
    {
        var fs = new RealFileSystem();
        string root = fs.Path.Combine(ResolvedTemporaryDirectory(fs), "redux-stock-test-" + Guid.NewGuid().ToString("N"));
        fs.Directory.CreateDirectory(root);
        try
        {
            string input = fs.Path.Combine(root, "stock.bin");
            string archive = fs.Path.Combine(root, "uninstall.zip");
            fs.File.WriteAllBytes(input, [1, 2, 3]);
            var manifest = new StockBaselineManifest("test", "disposable", [new StockBaselineFile("stock.bin", 3, Digest([1, 2, 3]))]);
            var service = new StockArchive(fs, new ZipFileService(fs));
            await service.CreateAsync(root, archive, manifest);
            byte[] old = fs.File.ReadAllBytes(archive);
            using (FileSystemStream held = fs.FileStream.New(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(async () => await service.CreateAsync(root, archive, manifest));
            }
            Assert.That(fs.File.ReadAllBytes(archive), Is.EqualTo(old));
            await service.VerifyAsync(archive, manifest);
            string restored = fs.Path.Combine(root, "restored");
            await service.RestoreToNewDirectoryAsync(archive, manifest, restored);
            Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(restored, "stock.bin")), Is.EqualTo(new byte[] { 1, 2, 3 }));
            await service.CreateAsync(root, archive, manifest);
            await service.VerifyAsync(archive, manifest);
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// ZIP64 central directories remain supported beyond the classic entry-count limit.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task Zip64EntryCount_IsFullyVerified()
    {
        var (fs, _, service) = Fixture();
        string[] directories = Enumerable.Range(0, 65536).Select(index => $"empty-{index:D5}").ToArray();
        var manifest = new StockBaselineManifest("zip64-synthetic", "disposable empty-directory contract", [], directories);
        byte[] bytes = Zip(manifest, directories.Select(directory => (directory + "/", Array.Empty<byte>(), 0)));
        Assert.That(bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x06, 0x06 }), Is.GreaterThanOrEqualTo(0),
            "The fixture must contain a ZIP64 end-of-central-directory record.");
        using var stream = new MemoryStream(bytes);
        await service.VerifyAsync(stream, manifest);
        Assert.That(fs.File.Exists(ARCHIVE), Is.False);
    }

    /// <summary>
    /// Creation detects source metadata changes during reading and cancellation after staging starts.
    /// </summary>
    /// <param name="fault">The injected source or cancellation fault.</param>
    /// <returns>The test task.</returns>
    [TestCase("source-changed")]
    [TestCase("cancel-during-copy")]
    public async Task InterruptedCreation_CleansPartialArchiveAndKeepsVerifiedOldOne(string fault)
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        byte[] old = fs.File.ReadAllBytes(ARCHIVE);
        using var cancellation = new CancellationTokenSource();
        var zip = new ZipFileService(fs);
        var factory = new Mock<IZipFileService>();
        factory.Setup(candidate => candidate.NewArchive(It.IsAny<Stream>(), It.IsAny<ZipArchiveMode>(), true))
            .Returns((Stream stream, ZipArchiveMode mode, bool leaveOpen) =>
            {
                IZipArchive archive = zip.NewArchive(stream, mode, leaveOpen);
                if (mode != ZipArchiveMode.Create)
                    return archive;
                var intercepted = new Mock<IZipArchive>();
                intercepted.Setup(candidate => candidate.CreateEntry(It.IsAny<string>(), It.IsAny<CompressionLevel>()))
                    .Returns((string name, CompressionLevel level) =>
                    {
                        IZipArchiveEntry entry = archive.CreateEntry(name, level);
                        if (name == "KSP2_x64.exe")
                        {
                            if (fault == "source-changed")
                            {
                                string path = fs.Path.Combine(SOURCE, name);
                                fs.File.SetLastWriteTimeUtc(path, fs.File.GetLastWriteTimeUtc(path).AddSeconds(5));
                            }
                            else
                            {
                                cancellation.Cancel();
                            }
                        }
                        return entry;
                    });
                intercepted.Setup(candidate => candidate.Dispose()).Callback(archive.Dispose);
                return intercepted.Object;
            });
        var failing = new StockArchive(fs, factory.Object, availableSpace: _ => long.MaxValue);
        await Assert.ThatAsync(async () => await failing.CreateAsync(SOURCE, ARCHIVE, manifest, cancellation.Token), Throws.Exception);
        Assert.That(fs.File.ReadAllBytes(ARCHIVE), Is.EqualTo(old));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
        await service.VerifyAsync(ARCHIVE, manifest);
    }

    /// <summary>
    /// Restore disk failures and cancellation remove partial staging trees and preserve the archive.
    /// </summary>
    /// <param name="fault">The injected restore fault.</param>
    /// <returns>The test task.</returns>
    [TestCase("write-failure")]
    [TestCase("cancel-during-copy")]
    [TestCase("space")]
    public async Task RestoreFailure_CleansStagingAndKeepsArchive(string fault)
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        byte[] old = fs.File.ReadAllBytes(ARCHIVE);
        using var cancellation = new CancellationTokenSource();
        var streams = new Mock<IFileStreamFactory>();
        streams.Setup(candidate => candidate.New(It.IsAny<string>(), It.IsAny<FileMode>(), It.IsAny<FileAccess>(),
                It.IsAny<FileShare>(), It.IsAny<int>(), It.IsAny<FileOptions>()))
            .Returns((string path, FileMode mode, FileAccess access, FileShare share, int buffer, FileOptions options) =>
            {
                if (path.Contains(".stock-restore-", StringComparison.Ordinal) && mode == FileMode.CreateNew)
                {
                    if (fault == "write-failure")
                        throw new IOException("Injected restore disk failure");
                    if (fault == "cancel-during-copy")
                        cancellation.Cancel();
                }
                return fs.FileStream.New(path, mode, access, share, buffer, options);
            });
        var intercepted = new Mock<IFileSystem>();
        intercepted.SetupGet(candidate => candidate.File).Returns(fs.File);
        intercepted.SetupGet(candidate => candidate.Directory).Returns(fs.Directory);
        intercepted.SetupGet(candidate => candidate.FileInfo).Returns(fs.FileInfo);
        intercepted.SetupGet(candidate => candidate.Path).Returns(fs.Path);
        intercepted.SetupGet(candidate => candidate.FileStream).Returns(streams.Object);
        var failing = new StockArchive(intercepted.Object, new ZipFileService(fs),
            availableSpace: _ => fault == "space" ? 0 : long.MaxValue);
        await Assert.ThatAsync(async () => await failing.RestoreToNewDirectoryAsync(ARCHIVE, manifest, RESTORED, cancellation.Token), Throws.Exception);
        Assert.That(fs.Directory.Exists(RESTORED), Is.False);
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
        Assert.That(fs.File.ReadAllBytes(ARCHIVE), Is.EqualTo(old));
        await service.VerifyAsync(ARCHIVE, manifest);
    }

    /// <summary>
    /// Content, metadata and entry budgets are enforced before any destination is published.
    /// </summary>
    /// <param name="budget">The restricted resource budget.</param>
    /// <returns>The test task.</returns>
    [TestCase("total")]
    [TestCase("file")]
    [TestCase("entries")]
    [TestCase("metadata")]
    [TestCase("archive")]
    public async Task ResourceBudget_IsEnforced(string budget)
    {
        StockArchiveLimits limits = budget switch
        {
            "total" => new StockArchiveLimits { MaxTotalBytes = 1 },
            "file" => new StockArchiveLimits { MaxFileBytes = 1 },
            "entries" => new StockArchiveLimits { MaxEntries = 1 },
            "metadata" => new StockArchiveLimits { MaxManifestBytes = 1 },
            _ => new StockArchiveLimits { MaxArchiveBytes = 1 }
        };
        var (fs, manifest, service) = Fixture(limits);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.CreateAsync(SOURCE, ARCHIVE, manifest));
        Assert.That(fs.File.Exists(ARCHIVE), Is.False);
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
    }

    /// <summary>
    /// Duplicate JSON keys and incompatible manifest versions cannot define an expected contract.
    /// </summary>
    /// <param name="kind">The malformed manifest shape.</param>
    [TestCase("duplicate")]
    [TestCase("version")]
    [TestCase("algorithm")]
    public void ManifestParser_RejectsAmbiguousOrUnsupportedContracts(string kind)
    {
        var (_, manifest, _) = Fixture();
        string json = Encoding.UTF8.GetString(manifest.ToJson());
        json = kind switch
        {
            "duplicate" => json.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal),
            "version" => json.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal),
            _ => json.Replace("SHA-256", "SHA-1", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidDataException>(() => StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Windows case collisions are rejected on a case-sensitive source filesystem too.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task CaseSensitiveDiskCollision_IsRejected()
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Linux));
        fs.Directory.CreateDirectory("/synthetic/source");
        fs.File.WriteAllBytes("/synthetic/source/stock.bin", [1]);
        fs.File.WriteAllBytes("/synthetic/source/STOCK.BIN", [1]);
        var manifest = new StockBaselineManifest("test", "disposable", [new StockBaselineFile("stock.bin", 1, Digest([1]))]);
        var service = new StockArchive(fs, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.CreateAsync("/synthetic/source", "/synthetic/uninstall.zip", manifest));
        Assert.That(fs.File.Exists("/synthetic/uninstall.zip"), Is.False);
    }

    /// <summary>
    /// A forged entry count or excessive directory budget is rejected before ZIP entry allocation.
    /// </summary>
    /// <param name="fault">The directory-envelope fault.</param>
    /// <returns>The test task.</returns>
    [TestCase("forged-count")]
    [TestCase("metadata-budget")]
    public async Task ZipEnvelope_IsBoundedBeforeOpeningZipService(string fault)
    {
        var (fs, manifest, _) = Fixture();
        var entries = Entries(fs, manifest).ToList();
        if (fault == "forged-count")
            entries.Add(("undeclared", [], 0));
        byte[] bytes = Zip(manifest, entries);
        if (fault == "forged-count")
        {
            ushort expectedCount = (ushort)(manifest.Files.Count + manifest.Directories.Count + 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), expectedCount);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), expectedCount);
        }
        var factory = new Mock<IZipFileService>(MockBehavior.Strict);
        var limits = new StockArchiveLimits { MaxCentralDirectoryBytes = fault == "metadata-budget" ? 1 : 32 * 1024 * 1024 };
        var service = new StockArchive(fs, factory.Object, limits);
        using var input = new MemoryStream(bytes);
        input.Position = 3;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.VerifyAsync(input, manifest));
        factory.Verify(candidate => candidate.NewArchive(It.IsAny<Stream>(), It.IsAny<ZipArchiveMode>(), It.IsAny<bool>()), Times.Never);
        Assert.That(input.Position, Is.EqualTo(3));
        Assert.That(input.CanRead, Is.True);
    }

    /// <summary>
    /// Disk destinations cannot use stream names, device namespaces or parent traversal.
    /// </summary>
    /// <param name="path">The invalid destination path.</param>
    /// <returns>The test task.</returns>
    [TestCase(@"C:\synthetic\uninstall.zip:stream")]
    [TestCase(@"C:\synthetic\NUL.zip")]
    [TestCase(@"C:\synthetic\..\outside.zip")]
    [TestCase(@"\\?\C:\synthetic\uninstall.zip")]
    [TestCase("//?/C:/synthetic/uninstall.zip")]
    public async Task UnsafeDiskDestination_IsRejected(string path)
    {
        var (fs, manifest, service) = Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.CreateAsync(SOURCE, path, manifest));
        Assert.That(fs.File.Exists(ARCHIVE), Is.False);
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
    }

    /// <summary>
    /// Publication failure retains the valid previous archive and cleans the verified staging file.
    /// </summary>
    /// <param name="cleanupFails">True to inject a cleanup failure too, false otherwise.</param>
    /// <returns>The test task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task SingleRenamePublicationFailure_PreservesPreviousArchiveAndReportsCleanup(bool cleanupFails)
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        byte[] old = fs.File.ReadAllBytes(ARCHIVE);
        var files = new Mock<IFile>();
        files.Setup(candidate => candidate.GetAttributes(It.IsAny<string>())).Returns((string path) => fs.File.GetAttributes(path));
        files.Setup(candidate => candidate.Exists(It.IsAny<string>())).Returns((string path) => fs.File.Exists(path));
        files.Setup(candidate => candidate.Move(It.IsAny<string>(), ARCHIVE, true))
            .Throws(new IOException("Injected rename publication failure"));
        files.Setup(candidate => candidate.Replace(It.IsAny<string>(), ARCHIVE, null))
            .Callback(() => fs.File.Delete(ARCHIVE))
            .Throws(new IOException("Injected legacy partial replacement failure"));
        files.Setup(candidate => candidate.Delete(It.IsAny<string>())).Callback((string path) =>
        {
            if (cleanupFails)
                throw new IOException("Injected cleanup failure");
            fs.File.Delete(path);
        });
        var intercepted = new Mock<IFileSystem>();
        intercepted.SetupGet(candidate => candidate.File).Returns(files.Object);
        intercepted.SetupGet(candidate => candidate.Directory).Returns(fs.Directory);
        intercepted.SetupGet(candidate => candidate.FileInfo).Returns(fs.FileInfo);
        intercepted.SetupGet(candidate => candidate.Path).Returns(fs.Path);
        intercepted.SetupGet(candidate => candidate.FileStream).Returns(fs.FileStream);
        var failing = new StockArchive(intercepted.Object, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        IOException? failure = await Assert.ThrowsAsync<IOException>(async () => await failing.CreateAsync(SOURCE, ARCHIVE, manifest));
        Assert.That(failure!.Message, Is.EqualTo("Injected rename publication failure"));
        Assert.That(fs.File.ReadAllBytes(ARCHIVE), Is.EqualTo(old));
        if (cleanupFails)
        {
            Assert.That(failure.Data["StockArchiveCleanupFailure"], Is.TypeOf<IOException>());
            Assert.That(fs.File.Exists((string)failure.Data["StockArchiveStagingPath"]!), Is.True);
        }
        else
        {
            Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
        }
        files.Verify(candidate => candidate.Move(It.IsAny<string>(), ARCHIVE, true), Times.Once);
        files.Verify(candidate => candidate.Replace(It.IsAny<string>(), ARCHIVE, null), Times.Never);
        await service.VerifyAsync(ARCHIVE, manifest);
    }

    /// <summary>
    /// An interruption after rename retains the exact verified new archive and stable source bytes.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task PublicationInterruptionAfterRename_RetainsVerifiedNewArchive()
    {
        var (fs, manifest, service) = Fixture();
        await service.CreateAsync(SOURCE, ARCHIVE, manifest);
        byte[] old = fs.File.ReadAllBytes(ARCHIVE);
        byte[] changed = [41, 42, 43, 44, 45];
        fs.File.WriteAllBytes(fs.Path.Combine(SOURCE, "KSP2_x64.exe"), changed);
        var target = new StockBaselineManifest("new-stock", "synthetic publication", manifest.Files.Select(file =>
            file.Path == "KSP2_x64.exe" ? new StockBaselineFile(file.Path, changed.Length, Digest(changed)) : file), manifest.Directories);
        var files = new Mock<IFile>();
        files.Setup(file => file.GetAttributes(It.IsAny<string>())).Returns((string path) => fs.File.GetAttributes(path));
        files.Setup(file => file.Exists(It.IsAny<string>())).Returns((string path) => fs.File.Exists(path));
        files.Setup(file => file.Delete(It.IsAny<string>())).Callback((string path) => fs.File.Delete(path));
        files.Setup(file => file.Move(It.IsAny<string>(), ARCHIVE, true)).Callback((string source, string destination, bool _) =>
        {
            fs.File.Move(source, destination, true);
            throw new IOException("Interrupted after successful rename");
        });
        var intercepted = new Mock<IFileSystem>();
        intercepted.SetupGet(fileSystem => fileSystem.File).Returns(files.Object);
        intercepted.SetupGet(fileSystem => fileSystem.Directory).Returns(fs.Directory);
        intercepted.SetupGet(fileSystem => fileSystem.Path).Returns(fs.Path);
        intercepted.SetupGet(fileSystem => fileSystem.FileInfo).Returns(fs.FileInfo);
        intercepted.SetupGet(fileSystem => fileSystem.FileStream).Returns(fs.FileStream);
        var interrupted = new StockArchive(intercepted.Object, new ZipFileService(fs), availableSpace: _ => long.MaxValue);
        await Assert.ThrowsAsync<IOException>(async () => await interrupted.CreateAsync(SOURCE, ARCHIVE, target));
        Assert.That(fs.File.ReadAllBytes(ARCHIVE), Is.Not.EqualTo(old));
        await service.VerifyAsync(ARCHIVE, target);
        Assert.That(fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, "KSP2_x64.exe")), Is.EqualTo(changed));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(ROOT, ".stock-*"), Is.Empty);
        files.Verify(file => file.Replace(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// A native destination sharing violation retains the exact prior archive during publication failure.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    [Platform("Win")]
    public async Task NativeLockedArchiveDestination_RetainsPreviousBytes()
    {
        var fs = new RealFileSystem();
        string root = fs.Path.Combine(ResolvedTemporaryDirectory(fs), $"redux-archive-publication-{Guid.NewGuid():N}");
        fs.Directory.CreateDirectory(root);
        try
        {
            string source = fs.Path.Combine(root, "stock.bin");
            string destination = fs.Path.Combine(root, "uninstall.zip");
            fs.File.WriteAllBytes(source, USER_ARCHIVE_BYTES);
            var manifest = new StockBaselineManifest("native-publication", "owned temporary tree",
                [new("stock.bin", USER_ARCHIVE_BYTES.Length, Digest(USER_ARCHIVE_BYTES))]);
            var service = new StockArchive(fs, new ZipFileService(fs));
            await service.CreateAsync(root, destination, manifest);
            byte[] old = fs.File.ReadAllBytes(destination);
            using (FileSystemStream held = fs.FileStream.New(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                IOException? failure = await Assert.ThrowsAsync<IOException>(async () => await service.CreateAsync(root, destination, manifest));
                Assert.That(failure!.InnerException, Is.TypeOf<System.ComponentModel.Win32Exception>());
                int error = ((System.ComponentModel.Win32Exception)failure.InnerException!).NativeErrorCode;
                Assert.That(error, Is.AnyOf(5, 32));
                Assert.That(fs.File.ReadAllBytes(destination), Is.EqualTo(old));
                TestContext.Progress.WriteLine($"Verified MoveFileExW failure {error} retains prior archive and source bytes.");
            }
            await service.VerifyAsync(destination, manifest);
            Assert.That(fs.File.ReadAllBytes(source), Is.EqualTo(USER_ARCHIVE_BYTES));
            Assert.That(fs.Directory.EnumerateFileSystemEntries(root, ".stock-*"), Is.Empty);
        }
        finally
        {
            fs.Directory.Delete(root, true);
        }
    }

    private static readonly byte[] USER_ARCHIVE_BYTES = [9, 8, 7, 6];

    private static string ResolvedTemporaryDirectory(IFileSystem fs)
    {
        // Resolve each existing ancestor so macOS /var does not masquerade as an owned tree.
        string path = fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(fs.Path.GetTempPath()));
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = fs.Path.GetDirectoryName(current))
        {
            ancestors.Push(current);
        }
        string resolved = ancestors.Pop();
        while (ancestors.Count != 0)
        {
            string component = fs.Path.GetFileName(ancestors.Pop());
            string candidate = fs.Path.Combine(resolved, component);
            resolved = fs.DirectoryInfo.New(candidate).ResolveLinkTarget(true)?.FullName ?? candidate;
        }
        return resolved;
    }

    /// <summary>
    /// Deflated content restores to verified seekable files for subsequent bounded range access.
    /// </summary>
    /// <returns>The test task.</returns>
    [Test]
    public async Task DeflatedArchive_RestoresVerifiedSeekableContent()
    {
        var (fs, manifest, service) = Fixture();
        fs.File.WriteAllBytes(ARCHIVE, Zip(manifest, Entries(fs, manifest), compression: CompressionLevel.SmallestSize));
        await service.VerifyAsync(ARCHIVE, manifest);
        await service.RestoreToNewDirectoryAsync(ARCHIVE, manifest, RESTORED);
        string path = fs.Path.Combine(RESTORED, BUNDLE.Replace('/', '\\'));
        byte[] expected = fs.File.ReadAllBytes(fs.Path.Combine(SOURCE, BUNDLE.Replace('/', '\\')));
        using FileSystemStream restored = fs.File.OpenRead(path);
        Assert.That(restored.CanSeek, Is.True);
        restored.Seek(7, SeekOrigin.Begin);
        Assert.That(restored.ReadByte(), Is.EqualTo((int)expected[7]));
        Assert.That(Digest(fs.File.ReadAllBytes(path)), Is.EqualTo(Digest(expected)));
    }
}
