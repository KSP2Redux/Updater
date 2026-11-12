using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

[NonParallelizable]
public class ShaderBundleCacheTest
{
    private string _directory = null!;
    private string _root = null!;
    private ShaderBundleCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Redux Shader Cache " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _root = Path.Combine(_directory, "shared");
        _cache = new ShaderBundleCache(new RealFileSystem(), _root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void PublishAndRestore_ShareVerifiedBytesAcrossIndependentCheckouts()
    {
        string key = Key("same build inputs");
        string source = WriteSource("first checkout", key);
        byte[] expected = File.ReadAllBytes(Path.Combine(source, "payload.bundle"));
        Assert.That(_cache.Publish(key, source), Is.True);
        Directory.Delete(source, true);
        var other = new ShaderBundleCache(new RealFileSystem(), _root);
        string output = Path.Combine(_directory, "second checkout", "published");

        Assert.That(other.TryRestore(key, output), Is.True);

        Assert.That(File.ReadAllBytes(Path.Combine(output, "payload.bundle")), Is.EqualTo(expected));
        ShaderManifest restored = ReadManifest(output);
        Assert.That(restored.Bundles.Single().Sha256, Is.EqualTo(Key(expected)));
        Assert.That(restored.SourceHash, Is.EqualTo(key));
    }

    [Test]
    public void RestoreAndRepublish_RefreshUseAndKeepAnOtherwiseExpiredEntry()
    {
        string key = Key("used build");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        DateTime now = DateTime.UtcNow;
        SetLastUse(key, now.AddDays(-15));

        Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "restored")), Is.True);
        Assert.That(_cache.Prune(now), Is.Zero);
        SetLastUse(key, now.AddDays(-15));
        Assert.That(_cache.Publish(key, source), Is.True);
        Assert.That(_cache.Prune(now), Is.Zero);
        Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "reused")), Is.True);
    }

    [TestCase("bundle bytes")]
    [TestCase("malformed json")]
    [TestCase("null manifest")]
    [TestCase("null bundles")]
    [TestCase("empty bundles")]
    [TestCase("null bundle")]
    [TestCase("duplicate bundle")]
    [TestCase("wrong key")]
    [TestCase("wrong version")]
    [TestCase("traversal")]
    [TestCase("rooted path")]
    [TestCase("windows drive")]
    [TestCase("directory marker")]
    public void Restore_RejectsCorruptOrEscapingEntriesWithoutChangingTheCheckout(string corruption)
    {
        string key = Key("corruption probe");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        string entry = Path.Combine(_root, key);
        ShaderManifest manifest = ReadManifest(entry);
        string manifestPath = Path.Combine(entry, "manifest.json");
        switch (corruption)
        {
            case "bundle bytes": File.AppendAllText(Path.Combine(entry, "payload.bundle"), "corruption"); break;
            case "malformed json": File.WriteAllText(manifestPath, "{ broken"); break;
            case "null manifest": File.WriteAllText(manifestPath, "null"); break;
            case "null bundles": manifest.Bundles = null!; WriteManifest(entry, manifest); break;
            case "empty bundles": manifest.Bundles.Clear(); WriteManifest(entry, manifest); break;
            case "null bundle": manifest.Bundles[0] = null!; WriteManifest(entry, manifest); break;
            case "duplicate bundle": manifest.Bundles.Add(manifest.Bundles[0]); WriteManifest(entry, manifest); break;
            case "wrong key": manifest.SourceHash = Key("different inputs"); WriteManifest(entry, manifest); break;
            case "wrong version": manifest.Version = 99; WriteManifest(entry, manifest); break;
            case "traversal": manifest.Bundles[0].File = "../outside.bundle"; WriteManifest(entry, manifest); break;
            case "rooted path": manifest.Bundles[0].File = Path.Combine(_directory, "outside.bundle"); WriteManifest(entry, manifest); break;
            case "windows drive": manifest.Bundles[0].File = "C:\\outside.bundle"; WriteManifest(entry, manifest); break;
            case "directory marker": File.Delete(Path.Combine(entry, ".last-used")); Directory.CreateDirectory(Path.Combine(entry, ".last-used")); break;
            default: throw new ArgumentException("Unknown corruption", nameof(corruption));
        }
        string destination = Path.Combine(_directory, "destination");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "existing checkout output");

        Assert.That(_cache.TryRestore(key, destination), Is.False);

        Assert.That(File.ReadAllText(Path.Combine(destination, "keep.txt")), Is.EqualTo("existing checkout output"));
        Assert.That(Directory.GetFiles(destination), Has.Length.EqualTo(1));
    }

    [TestCase(".last-used")]
    [TestCase("manifest.json")]
    [TestCase("content.txt")]
    public void Publish_RejectsMetadataNamesAndNonBundlePayloads(string name)
    {
        string key = Key("reserved filename");
        string source = WriteSource("invalid checkout", key, name);

        Assert.Throws<InvalidDataException>(() => _cache.Publish(key, source));

        Assert.That(Directory.Exists(Path.Combine(_root, key)), Is.False);
    }

    [Test]
    public void Publish_RejectsCorruptSourceAndRepairsAnInvalidCachedEntry()
    {
        string key = Key("repairable build");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        File.WriteAllText(Path.Combine(_root, key, "payload.bundle"), "bad cached bytes");
        Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "rejected")), Is.False);

        Assert.That(_cache.Publish(key, source), Is.True);
        Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "repaired")), Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(_directory, "repaired", "payload.bundle")),
            Is.EqualTo(File.ReadAllBytes(Path.Combine(source, "payload.bundle"))));

        File.WriteAllText(Path.Combine(source, "payload.bundle"), "bad source bytes");
        Assert.Throws<InvalidDataException>(() => _cache.Publish(key, source));
        Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "still verified")), Is.True);
    }

    [Test]
    public void PublishAndRestore_AcceptUppercaseBundleHashesWithoutChangingBytes()
    {
        string key = Key("uppercase checksum");
        string source = WriteSource("checkout", key);
        ShaderManifest manifest = ReadManifest(source);
        manifest.Bundles[0].Sha256 = manifest.Bundles[0].Sha256!.ToUpperInvariant();
        WriteManifest(source, manifest);

        Assert.That(_cache.Publish(key, source), Is.True);
        string destination = Path.Combine(_directory, "restored");
        Assert.That(_cache.TryRestore(key, destination), Is.True);

        Assert.That(File.ReadAllBytes(Path.Combine(destination, "payload.bundle")),
            Is.EqualTo(File.ReadAllBytes(Path.Combine(source, "payload.bundle"))));
    }

    [TestCase("../outside")]
    [TestCase("..\\outside")]
    public void CacheKeys_CannotEscapeToAnotherDirectory(string key)
    {
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside data");
        string source = WriteSource("checkout", Key("valid source"));

        Assert.Throws<ArgumentException>(() => _cache.Publish(key, source));
        Assert.Throws<ArgumentException>(() => _cache.TryRestore(key, Path.Combine(_directory, "destination")));

        Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("outside data"));
        Assert.That(Directory.GetFiles(outside), Has.Length.EqualTo(1));
    }

    [Test]
    public void Prune_ExpiresAnOwnedEntryEvenWhenItsBundleBecameCorrupt()
    {
        string key = Key("old damaged build");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        string entry = Path.Combine(_root, key);
        File.WriteAllText(Path.Combine(entry, "payload.bundle"), "damaged cached payload");
        DateTime now = DateTime.UtcNow;
        SetLastUse(key, now.AddDays(-15));

        Assert.That(_cache.Prune(now), Is.EqualTo(1));

        Assert.That(Directory.Exists(entry), Is.False);
        Assert.That(ReadManifest(source).SourceHash, Is.EqualTo(key));
        Assert.That(File.ReadAllText(Path.Combine(source, "payload.bundle")), Does.StartWith("compiled shader bytes"));
    }

    [Test]
    public void Prune_RemovesOnlyExpiredManagedEntriesAndAbandonedPublications()
    {
        DateTime now = DateTime.UtcNow;
        string old = Key("old build");
        string recent = Key("recent build");
        _cache.Publish(old, WriteSource("old checkout", old));
        _cache.Publish(recent, WriteSource("recent checkout", recent));
        SetLastUse(old, now.AddDays(-15));
        SetLastUse(recent, now.AddDays(-2));
        string partialKey = Key("interrupted build");
        string abandoned = CreateOldDirectory(partialKey + ".partial-" + Guid.NewGuid().ToString("N"), now.AddDays(-15));
        string legacy = CreateOldDirectory(Key("legacy partial") + ".partial", now.AddDays(-15));
        string active = CreateOldDirectory(Key("recent partial") + ".partial-" + Guid.NewGuid().ToString("N"), now.AddDays(-1));
        string unrelated = CreateOldDirectory("other-tool", now.AddDays(-90));
        string malformedPartial = CreateOldDirectory(Key("not a publication") + ".partial-user-data", now.AddDays(-90));
        string unowned = CreateOldDirectory(Key("unowned hash directory"), now.AddDays(-90));

        Assert.That(_cache.Prune(now), Is.EqualTo(3));

        Assert.That(Directory.Exists(Path.Combine(_root, old)), Is.False);
        Assert.That(Directory.Exists(abandoned), Is.False);
        Assert.That(Directory.Exists(legacy), Is.False);
        foreach (string preserved in new[] { Path.Combine(_root, recent), active, unrelated, malformedPartial, unowned })
            Assert.That(Directory.Exists(preserved), Is.True, preserved);
        Assert.That(File.ReadAllText(Path.Combine(unrelated, "keep.txt")), Is.EqualTo("unrelated data"));
        Assert.That(_cache.Prune(now), Is.Zero);
    }

    [Test]
    public void ActiveEntryLease_BlocksRestorePublicationAndExpiryUntilReleased()
    {
        string key = Key("leased build");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        DateTime now = DateTime.UtcNow;
        SetLastUse(key, now.AddDays(-15));
        string path = Path.Combine(_root, ".locks", key + ".lock");
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "busy restore")), Is.False);
            Assert.That(_cache.Publish(key, source), Is.False);
            Assert.That(_cache.Prune(now), Is.Zero);
            Assert.That(Directory.Exists(Path.Combine(_root, key)), Is.True);
        }

        Assert.That(_cache.Prune(now), Is.EqualTo(1));
        Assert.That(File.Exists(path), Is.True);
        Assert.That(_cache.Publish(key, source), Is.True);
    }

    [Test]
    public void RestoreAndPublish_RejectPathsOverlappingTheSharedCache()
    {
        string key = Key("overlap probe");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        string otherKey = Key("other build");
        string otherSource = WriteSource("other checkout", otherKey);
        _cache.Publish(otherKey, otherSource);
        byte[] otherBytes = File.ReadAllBytes(Path.Combine(_root, otherKey, "payload.bundle"));

        foreach (string destination in new[]
        {
            _root, Path.Combine(_root, otherKey), _directory,
            Path.Combine(_root, "..", "shared", otherKey)
        })
            Assert.Throws<IOException>(() => _cache.TryRestore(key, destination), destination);
        foreach (string cachedSource in new[] { _root, Path.Combine(_root, key), _directory })
            Assert.Throws<IOException>(() => _cache.Publish(key, cachedSource), cachedSource);

        Assert.That(File.ReadAllBytes(Path.Combine(_root, otherKey, "payload.bundle")), Is.EqualTo(otherBytes));
        Assert.That(_cache.TryRestore(otherKey, Path.Combine(_directory, "other restored")), Is.True);
    }

    [Test]
    public void CheckoutPaths_WithTheSamePrefixRemainSeparateFromTheCache()
    {
        string key = Key("prefix sibling");
        string source = WriteSource("shared-checkout", key);

        Assert.That(_cache.Publish(key, source), Is.True);
        string destination = Path.Combine(_directory, "shared-restored");
        Assert.That(_cache.TryRestore(key, destination), Is.True);

        Assert.That(File.ReadAllBytes(Path.Combine(destination, "payload.bundle")),
            Is.EqualTo(File.ReadAllBytes(Path.Combine(source, "payload.bundle"))));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DirectoryLinks_IncludingDanglingOnesCannotRedirectTheCacheRoot(bool dangling)
    {
        string target = Path.Combine(_directory, "outside target");
        if (!dangling)
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "outside bytes");
        }
        CreateDirectoryLink(_root, target);

        Assert.Throws<IOException>(() => new ShaderBundleCache(new RealFileSystem(), _root));
        Assert.Throws<IOException>(() => _cache.Prune());

        if (dangling)
            Assert.That(Directory.Exists(target), Is.False);
        else
            Assert.That(File.ReadAllText(Path.Combine(target, "keep.txt")), Is.EqualTo("outside bytes"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DirectoryLinks_CannotReplaceEntriesOrRedirectCheckoutOutput(bool dangling)
    {
        string key = Key("directory link probe");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        string entry = Path.Combine(_root, key);
        string target = Path.Combine(_directory, "external entry");
        if (dangling)
            Directory.Delete(entry, true);
        else
            Directory.Move(entry, target);
        CreateDirectoryLink(entry, target);

        Assert.That(_cache.TryRestore(key, Path.Combine(_directory, "rejected")), Is.False);
        Assert.That(_cache.Prune(DateTime.UtcNow.AddDays(15)), Is.Zero);
        Assert.Throws<IOException>(() => _cache.Publish(key, source));
        Assert.That(Directory.Exists(target), Is.EqualTo(!dangling));
        if (!dangling)
            Assert.That(File.ReadAllBytes(Path.Combine(target, "payload.bundle")),
                Is.EqualTo(File.ReadAllBytes(Path.Combine(source, "payload.bundle"))));

        string safeKey = Key("safe output probe");
        _cache.Publish(safeKey, WriteSource("safe checkout", safeKey));
        string linkedOutput = Path.Combine(_directory, "linked output");
        string outputTarget = Path.Combine(_directory, "output target");
        if (!dangling)
        {
            Directory.CreateDirectory(outputTarget);
            File.WriteAllText(Path.Combine(outputTarget, "keep.txt"), "outside output");
        }
        CreateDirectoryLink(linkedOutput, outputTarget);

        Assert.Throws<IOException>(() => _cache.TryRestore(safeKey, linkedOutput));
        Assert.That(Directory.Exists(outputTarget), Is.EqualTo(!dangling));
        if (!dangling)
            Assert.That(Directory.GetFiles(outputTarget), Has.Length.EqualTo(1));
    }

    [Test]
    public void Publish_CannotReadThroughALinkedSourceDirectory()
    {
        string key = Key("source link probe");
        string source = WriteSource("real checkout", key);
        string linked = Path.Combine(_directory, "linked checkout");
        CreateDirectoryLink(linked, source);

        Assert.Throws<InvalidDataException>(() => _cache.Publish(key, linked));

        Assert.That(Directory.Exists(Path.Combine(_root, key)), Is.False);
        Assert.That(ReadManifest(source).SourceHash, Is.EqualTo(key));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FileLinks_CannotRedirectAnEntryLease(bool dangling)
    {
        string key = Key("lease link probe");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        string path = Path.Combine(_root, ".locks", key + ".lock");
        string target = Path.Combine(_directory, "external lock");
        if (!dangling)
            File.WriteAllText(target, "outside lease data");
        File.Delete(path);
        CreateFileLink(path, target);

        Assert.Throws<IOException>(() => _cache.TryRestore(key, Path.Combine(_directory, "rejected")));
        Assert.Throws<IOException>(() => _cache.Publish(key, source));
        Assert.That(_cache.Prune(DateTime.UtcNow.AddDays(15)), Is.Zero);
        Assert.That(File.Exists(target), Is.EqualTo(!dangling));
        if (!dangling)
            Assert.That(File.ReadAllText(target), Is.EqualTo("outside lease data"));
    }

    [TestCase("manifest.json", false)]
    [TestCase("manifest.json", true)]
    [TestCase("payload.bundle", false)]
    [TestCase("payload.bundle", true)]
    [TestCase(".last-used", false)]
    [TestCase(".last-used", true)]
    public void FileLinks_CannotRedirectCacheReadsWritesOrExpiry(string name, bool dangling)
    {
        string key = Key("file link probe");
        string source = WriteSource("checkout", key);
        _cache.Publish(key, source);
        string entry = Path.Combine(_root, key);
        string path = Path.Combine(entry, name);
        string target = Path.Combine(_directory, "external " + name);
        if (!dangling)
            File.Copy(path, target);
        File.Delete(path);
        CreateFileLink(path, target);
        string destination = Path.Combine(_directory, "rejected");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "existing checkout output");

        Assert.That(_cache.TryRestore(key, destination), Is.False);

        Assert.That(File.ReadAllText(Path.Combine(destination, "keep.txt")), Is.EqualTo("existing checkout output"));
        Assert.That(Directory.GetFiles(destination), Has.Length.EqualTo(1));
        Assert.That(_cache.Prune(DateTime.UtcNow.AddDays(15)), Is.Zero);
        Assert.That(File.Exists(target), Is.EqualTo(!dangling));
        Assert.That(File.GetAttributes(path) & FileAttributes.ReparsePoint, Is.EqualTo(FileAttributes.ReparsePoint));
    }

    [Test]
    public async Task ConcurrentPublishRestoreAndPrune_NeverAcceptPartialBundles()
    {
        string key = Key("concurrent build");
        string source = WriteSource("checkout", key);
        byte[] expected = File.ReadAllBytes(Path.Combine(source, "payload.bundle"));
        _cache.Publish(key, source);
        using var start = new ManualResetEventSlim();
        Task writer = Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (int index = 0; index < 30; index++)
            {
                _cache.Publish(key, source);
                Thread.Yield();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task reader = Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (int index = 0; index < 30; index++)
            {
                string destination = Path.Combine(_directory, "concurrent checkout " + index);
                if (_cache.TryRestore(key, destination))
                {
                    Assert.That(File.ReadAllBytes(Path.Combine(destination, "payload.bundle")), Is.EqualTo(expected));
                    Assert.That(ReadManifest(destination).SourceHash, Is.EqualTo(key));
                }
                Thread.Yield();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task pruner = Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (int index = 0; index < 30; index++)
            {
                _cache.Prune(DateTime.UtcNow.AddDays(15));
                Thread.Yield();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        start.Set();
        await Task.WhenAll(writer, reader, pruner);

        Assert.That(_cache.Publish(key, source), Is.True);
        string final = Path.Combine(_directory, "final checkout");
        Assert.That(_cache.TryRestore(key, final), Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(final, "payload.bundle")), Is.EqualTo(expected));
        Assert.That(Directory.GetDirectories(_root, "*.partial-*"), Is.Empty);
    }

    private string WriteSource(string name, string key, string file = "payload.bundle")
    {
        string directory = Path.Combine(_directory, name);
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.UTF8.GetBytes("compiled shader bytes from " + name);
        File.WriteAllBytes(Path.Combine(directory, file), bytes);
        WriteManifest(directory, new ShaderManifest
        {
            Version = 1,
            UnityVersion = "6000.6.8f1",
            Platform = "StandaloneWindows64",
            SourceHash = key,
            Bundles = [new ShaderManifestBundle { File = file, Sha256 = Key(bytes) }]
        });
        return directory;
    }

    private static void WriteManifest(string directory, ShaderManifest manifest) =>
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));

    private static ShaderManifest ReadManifest(string directory) =>
        JsonSerializer.Deserialize<ShaderManifest>(File.ReadAllBytes(Path.Combine(directory, "manifest.json")))!;

    private static string Key(string value) => Key(Encoding.UTF8.GetBytes(value));
    private static string Key(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private void SetLastUse(string key, DateTime utc) =>
        File.SetLastWriteTimeUtc(Path.Combine(_root, key, ".last-used"), utc);

    private string CreateOldDirectory(string name, DateTime utc)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "keep.txt"), "unrelated data");
        Directory.SetLastWriteTimeUtc(path, utc);
        return path;
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        using Process process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.That(process.WaitForExit(10000), Is.True);
        Assert.That(process.ExitCode, Is.Zero);
    }

    private static void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Ignore("This file-symlink case needs Windows Developer Mode or symbolic-link privilege. Directory junction coverage still runs.");
        }
        catch (IOException exception) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Ignore("Windows rejected file-symlink creation without symbolic-link privilege. Directory junction coverage still runs.");
        }
    }
}
