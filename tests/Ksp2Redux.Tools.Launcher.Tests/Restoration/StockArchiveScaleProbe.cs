using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Restoration;

/// <summary>
/// Opt-in archive creation and independent restoration verification on a staged stock baseline.
/// </summary>
[Category("Integration")]
[NonParallelizable]
public class StockArchiveScaleProbe
{
    private const string CONFIG_VARIABLE = "KSP2REDUX_STOCK_ARCHIVE_PROBE";

    /// <summary>
    /// Create, verify and restore a configured full archive, retaining diagnostic artifacts.
    /// </summary>
    /// <returns>The probe task.</returns>
    [Test]
    public async Task ConfiguredBaseline_RestoresExactTreeAndKeepsProbeArtifactsAsync()
    {
        string? configPath = SystemEnvironmentProvider.Instance.GetEnvironmentVariable(CONFIG_VARIABLE);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            Assert.Ignore($"Set {CONFIG_VARIABLE} to an explicit disposable probe configuration.");
            return;
        }

        IFileSystem fileSystem = new RealFileSystem();
        EnsureSafePath(fileSystem, configPath, true);
        using JsonDocument config = JsonDocument.Parse(fileSystem.File.ReadAllBytes(configPath));
        JsonElement values = config.RootElement;
        string checkout = FullPath(fileSystem, RequiredPath(fileSystem, values, "checkoutRoot"));
        string source = FullPath(fileSystem, RequiredPath(fileSystem, values, "sourceRoot"));
        string manifestPath = FullPath(fileSystem, RequiredPath(fileSystem, values, "manifestPath"));
        string output = FullPath(fileSystem, RequiredPath(fileSystem, values, "outputRoot"));
        Assert.That(fileSystem.File.Exists(fileSystem.Path.Combine(checkout, "ksp2redux.tools", "Ksp2Redux.Tools.slnx")),
            Is.True, "checkoutRoot must identify the Redux checkout containing this tools solution.");
        string reference = fileSystem.Path.Combine(checkout, "reference", "omnipatching");
        string library = fileSystem.Path.Combine(checkout, "Library", "Omnipatching");
        Assert.That(IsWithin(source, reference), Is.True, "Source must be a staged reference/omnipatching tree.");
        Assert.That(IsWithin(output, reference) || IsWithin(output, library), Is.True,
            "Output must be a fresh child of reference/omnipatching or Library/Omnipatching.");
        Assert.That(IsWithin(source, output) || IsWithin(output, source) || source.Equals(output, StringComparison.OrdinalIgnoreCase),
            Is.False, "Source and output cannot overlap.");
        Assert.That(IsWithin(manifestPath, output) || IsWithin(FullPath(fileSystem, configPath), output), Is.False,
            "Probe input files cannot be inside the output tree.");
        EnsureSafePath(fileSystem, checkout, true);
        EnsureSafePath(fileSystem, source, true);
        EnsureSafePath(fileSystem, manifestPath, true);
        EnsureSafePath(fileSystem, fileSystem.Path.GetDirectoryName(output)!, true);
        EnsureSafePath(fileSystem, output, false);
        Assert.That(fileSystem.Directory.Exists(output) || fileSystem.File.Exists(output), Is.False,
            "Refusing a preexisting output path.");

        byte[] manifestBytes = fileSystem.File.ReadAllBytes(manifestPath);
        StockBaselineManifest expected = StockBaselineManifest.FromJson(manifestBytes);
        string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        string expectedTreeHash = TreeHash(expected.Files, expected.Directories);
        string archivePath = fileSystem.Path.Combine(output, "uninstall.zip");
        string restored = fileSystem.Path.Combine(output, "restored");
        string generationPath = fileSystem.Path.Combine(output, "generation.json");
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        var generation = new Dictionary<string, object?>
        {
            ["schema"] = "omnipatching.stock-archive-scale-probe.v1",
            ["status"] = "running",
            ["startedUtc"] = DateTimeOffset.UtcNow,
            ["configPath"] = FullPath(fileSystem, configPath),
            ["checkoutRoot"] = checkout,
            ["sourceRoot"] = source,
            ["expectedManifestPath"] = manifestPath,
            ["expectedManifestSha256"] = manifestHash,
            ["expectedTreeSha256"] = expectedTreeHash,
            ["outputRoot"] = output,
            ["archivePath"] = archivePath,
            ["restoredRoot"] = restored,
            ["expectedFileCount"] = expected.Files.Count,
            ["expectedDirectoryCount"] = expected.Directories.Count,
            ["expectedBytes"] = expected.TotalBytes,
            ["treeHashFormat"] = "Ordinal directories: D\\0path\\n. Ordinal files: F\\0path\\0decimal-length\\0uppercase-SHA256\\n. UTF-8 SHA256.",
            ["provenance"] = expected.Provenance,
            ["authenticityLimitation"] = "Agreement with the supplied manifest does not independently authenticate its provenance.",
            ["elapsedStagesSeconds"] = stages
        };
        fileSystem.Directory.CreateDirectory(output);
        EnsureSafePath(fileSystem, output, true);
        Assert.That(fileSystem.Directory.EnumerateFileSystemEntries(output), Is.Empty);
        var service = new StockArchive(fileSystem, new ZipFileService(fileSystem));
        var total = Stopwatch.StartNew();
        try
        {
            await StageAsync("create", () => service.CreateAsync(source, archivePath, expected));
            await StageAsync("verify", () => service.VerifyAsync(archivePath, expected));
            await StageAsync("restore", () => service.RestoreToNewDirectoryAsync(archivePath, expected, restored));
            await StageAsync("independent-restored-tree", async () =>
            {
                var actualFiles = new List<StockBaselineFile>();
                var actualDirectories = new List<string>();
                var pending = new Stack<string>();
                pending.Push(restored);
                while (pending.Count > 0)
                {
                    string directory = pending.Pop();
                    EnsureSafePath(fileSystem, directory, true);
                    foreach (string path in fileSystem.Directory.EnumerateFileSystemEntries(directory))
                    {
                        EnsureSafePath(fileSystem, path, true);
                        string relative = fileSystem.Path.GetRelativePath(restored, path).Replace('\\', '/');
                        if ((fileSystem.File.GetAttributes(path) & FileAttributes.Directory) != 0)
                        {
                            actualDirectories.Add(relative);
                            pending.Push(path);
                            continue;
                        }
                        var before = fileSystem.FileInfo.New(path);
                        long length = before.Length;
                        DateTime writeTime = before.LastWriteTimeUtc;
                        string digest = await HashFileAsync(fileSystem, path);
                        var after = fileSystem.FileInfo.New(path);
                        EnsureSafePath(fileSystem, path, true);
                        Assert.That(after.Length == length && after.LastWriteTimeUtc == writeTime, Is.True,
                            $"Restored file changed while hashing: {relative}");
                        actualFiles.Add(new StockBaselineFile(relative, length, digest));
                    }
                }
                actualFiles.Sort((first, second) => StringComparer.Ordinal.Compare(first.Path, second.Path));
                actualDirectories.Sort(StringComparer.Ordinal);
                Assert.That(actualFiles.Select(file => file.Path), Is.EqualTo(expected.Files.Select(file => file.Path)));
                Assert.That(actualDirectories, Is.EqualTo(expected.Directories), "Includes empty directories.");
                foreach ((StockBaselineFile actual, StockBaselineFile declared) in actualFiles.Zip(expected.Files))
                {
                    Assert.That(actual.Length, Is.EqualTo(declared.Length), actual.Path);
                    Assert.That(actual.Sha256, Is.EqualTo(declared.Sha256), actual.Path);
                }
                string treeHash = TreeHash(actualFiles, actualDirectories);
                Assert.That(treeHash, Is.EqualTo(expectedTreeHash));
                generation["restoredTreeSha256"] = treeHash;
                generation["restoredFileCount"] = actualFiles.Count;
                generation["restoredDirectoryCount"] = actualDirectories.Count;
                generation["restoredBytes"] = actualFiles.Sum(file => file.Length);
            });
            await StageAsync("archive-hash", async () =>
            {
                generation["archiveLength"] = fileSystem.FileInfo.New(archivePath).Length;
                generation["archiveSha256"] = await HashFileAsync(fileSystem, archivePath);
            });
            Assert.That(await HashFileAsync(fileSystem, manifestPath), Is.EqualTo(manifestHash), "Expected manifest changed.");
            generation["status"] = "complete";
        }
        catch (Exception error)
        {
            generation["status"] = "failed";
            generation["error"] = error.ToString();
            throw;
        }
        finally
        {
            generation["finishedUtc"] = DateTimeOffset.UtcNow;
            generation["elapsedTotalSeconds"] = total.Elapsed.TotalSeconds;
            // This directory was created by the probe. No preexisting artifacts are cleaned up.
            fileSystem.File.WriteAllText(generationPath, JsonSerializer.Serialize(generation,
                new JsonSerializerOptions { WriteIndented = true }));
            TestContext.Progress.WriteLine($"Probe artifacts retained at {output}");
        }

        async Task StageAsync(string name, Func<Task> operation)
        {
            TestContext.Progress.WriteLine($"{DateTimeOffset.UtcNow:O} START {name}");
            var watch = Stopwatch.StartNew();
            try
            {
                await operation();
                TestContext.Progress.WriteLine($"{DateTimeOffset.UtcNow:O} END {name} {watch.Elapsed.TotalSeconds:F3}s");
            }
            catch
            {
                TestContext.Progress.WriteLine($"{DateTimeOffset.UtcNow:O} FAILED {name} {watch.Elapsed.TotalSeconds:F3}s");
                throw;
            }
            finally
            {
                stages[name] = watch.Elapsed.TotalSeconds;
            }
        }
    }

    private static string RequiredPath(IFileSystem fileSystem, JsonElement config, string name)
    {
        string? value = config.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || !fileSystem.Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Probe '{name}' must be an explicit absolute path.");
        return value;
    }

    private static string FullPath(IFileSystem fileSystem, string path)
        => fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(path));

    private static bool IsWithin(string path, string root)
        => path.StartsWith(root.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(root.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafePath(IFileSystem fileSystem, string path, bool mustExist)
    {
        string current = fileSystem.Path.GetFullPath(path);
        bool first = true;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((fileSystem.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Probe refuses a link or junction: {current}");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                if (mustExist || !first)
                    throw new DirectoryNotFoundException($"Probe path does not exist: {current}", error);
            }
            first = false;
            string? parent = fileSystem.Path.GetDirectoryName(current);
            if (parent == current)
                break;
            current = parent ?? "";
        }
    }

    private static async Task<string> HashFileAsync(IFileSystem fileSystem, string path)
    {
        EnsureSafePath(fileSystem, path, true);
        await using FileSystemStream stream = fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream);
        EnsureSafePath(fileSystem, path, true);
        return Convert.ToHexString(hash);
    }

    private static string TreeHash(IEnumerable<StockBaselineFile> files, IEnumerable<string> directories)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string directory in directories.OrderBy(path => path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"D\0{directory}\n"));
        }
        foreach (StockBaselineFile file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"F\0{file.Path}\0{file.Length.ToString(CultureInfo.InvariantCulture)}\0{file.Sha256.ToUpperInvariant()}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
