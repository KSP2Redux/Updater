using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;
using GameDistribution = Ksp2Redux.Tools.Launcher.Models.Distribution;

namespace Ksp2Redux.Tools.Launcher.Tests;

/// <summary>
/// Installs a local Redux patch, verifies bundle conversion, and checks that uninstall restores every
/// file byte for byte. Opt in with REDUX_E2E_INSTALL_DIR and REDUX_E2E_PATCH on a disposable KSP2 copy.
/// </summary>
[Category("Integration")]
[NonParallelizable]
public class InstallRevertEndToEndTest
{
    /// <summary>
    /// Exercise the production install and uninstall plans against an explicitly supplied game folder.
    /// </summary>
    /// <returns>The asynchronous test execution.</returns>
    [Test]
    public async Task InstallAndUninstall_RestoresEveryStockFileAsync()
    {
        var environment = SystemEnvironmentProvider.Instance;
        var installInput = environment.GetEnvironmentVariable("REDUX_E2E_INSTALL_DIR");
        if (string.IsNullOrWhiteSpace(installInput))
        {
            // This project uses NUnit. Ignore is its dynamic skip, and TestContext.Out is its test output helper.
            Assert.Ignore("Set REDUX_E2E_INSTALL_DIR and REDUX_E2E_PATCH to run the install/revert end-to-end test.");
        }

        IFileSystem fileSystem = new RealFileSystem();
        var install = GetAllowedPath(fileSystem, installInput!);
        var patchInput = environment.GetEnvironmentVariable("REDUX_E2E_PATCH");
        Assert.That(patchInput, Is.Not.Null.And.Not.Empty, "REDUX_E2E_PATCH is required when opting in.");
        var patch = GetAllowedPath(fileSystem, patchInput!);
        Assert.That(IsWithin(fileSystem, patch, install), Is.False, "Keep the input patch outside the install folder.");
        Assert.That(fileSystem.Directory.Exists(install), Is.True, "The install folder must exist.");
        Assert.That(fileSystem.File.Exists(fileSystem.Path.Combine(install, Ksp2Install.KSP2_EXE_NAME)), Is.True,
            "The install folder must contain KSP2_x64.exe.");
        Assert.That(fileSystem.File.Exists(patch), Is.True, "The local Redux patch must exist.");

        var distributionInput = environment.GetEnvironmentVariable("REDUX_E2E_DISTRIBUTION") ?? "Steam";
        var distribution = distributionInput.ToUpperInvariant() switch
        {
            "STEAM" => GameDistribution.Steam,
            "EPIC" => GameDistribution.Epic,
            "PORTABLE" => GameDistribution.Portable,
            _ => throw new ArgumentException("REDUX_E2E_DISTRIBUTION must be Steam, Epic, or Portable.")
        };

        var reportInput = environment.GetEnvironmentVariable("REDUX_E2E_REPORT_DIR");
        var report = string.IsNullOrWhiteSpace(reportInput) ? null : GetAllowedPath(fileSystem, reportInput);
        if (report is not null)
        {
            Assert.That(IsWithin(fileSystem, report, install), Is.False, "Keep reports outside the install folder.");
        }

        var output = TextWriter.Synchronized(TestContext.Out);
        var conversionFailures = new ConcurrentQueue<string>();
        void Log(string message)
        {
            output.WriteLine(message);
            if (message.StartsWith("Could not convert:", StringComparison.Ordinal) ||
                message.StartsWith("Left alone, the bundle holds unexpected bytes:", StringComparison.Ordinal) ||
                message.StartsWith("Bundle conversion failed:", StringComparison.Ordinal))
            {
                conversionFailures.Enqueue(message);
            }
        }

        // Match the launcher DI's core services. Configuration stays in memory and downloads are disabled
        // because the plan consumes a local patch. Progress and notifications go to the test output.
        var zip = new ZipFileService(fileSystem);
        var cache = new CacheService(fileSystem, zip);
        var modules = new ModuleDefinitionService();
        var config = new Mock<ILauncherConfigService>(MockBehavior.Strict);
        config.SetupGet(service => service.Config).Returns(new LauncherConfig("unused-e2e-config.json"));
        var service = new InstallPlanService(fileSystem, cache, environment, new ExecutingAssemblyService(),
            modules, zip, new DiskSpaceService(fileSystem), new LocalPatchDownloads(), config.Object,
            new BundleConversionService(fileSystem));

        Task ExecuteAsync(InstallPlan plan) => service.ApplyToFolder(plan, install, Log,
            (_, _) => { }, (done, total) => Log($"Plan steps: {done}/{total}"), CancellationToken.None);

        Task UninstallAsync()
        {
            var plan = new InstallPlan();
            plan.Uninstall();
            return ExecuteAsync(plan);
        }

        var cachePath = fileSystem.Path.Combine(install, "uninstall.zip");
        await TimeAsync("1. Normalize to stock", async () =>
        {
            if (fileSystem.File.Exists(cachePath))
            {
                await UninstallAsync();
            }
            else
            {
                Log("No uninstall.zip. Using the supplied stock folder.");
            }
        }, Log);

        // Prepatch selects the distribution by stock markers. Validate the requested enum rather than
        // changing those markers or replacing the production detector or embedded prepatch resources.
        var stockInstall = new Ksp2Install(fileSystem, modules, fileSystem.Path.Combine(install, Ksp2Install.KSP2_EXE_NAME));
        Assert.That(stockInstall.Distribution, Is.EqualTo(distribution),
            "The normalized stock folder must match REDUX_E2E_DISTRIBUTION.");
        Log($"Distribution: {distribution}. Install: {install}. Patch: {patch}.");

        var before = Snapshot(fileSystem, install, "2. Stock snapshot", Log);
        WriteSnapshot(fileSystem, report, "stock-snapshot.json", before);

        Exception? installFailure = null;
        try
        {
            // These helpers prepend steps. Prepatch creates uninstall.zip internally before applying
            // the embedded distribution prepatch, and the plan converts bundles after the local patch.
            var plan = new InstallPlan();
            plan.ApplyPatchFile(patch);
            plan.Prepatch();
            service.Describe(plan, Log);
            await TimeAsync("3. Install and convert bundles", () => ExecuteAsync(plan), Log);

            await TimeAsync("4. Verify installed bundles", () =>
            {
                Assert.That(fileSystem.File.Exists(fileSystem.Path.Combine(install, BundleConversionService.SHADER_MANIFEST)),
                    Is.True, "The patch must contain the Windows URP shader manifest.");
                var converter = new BundleConverter(fileSystem, install);
                Assert.That(fileSystem.File.Exists(converter.Layout.JournalPath), Is.True, "Conversion must write its journal.");
                var verify = converter.Verify();
                var problems = verify.Edits.Where(edit => edit.State != EditState.Applied)
                    .Select(edit => $"{edit.State}: {edit.File} @ {edit.Offset}")
                    .Concat(verify.Outputs.Where(copy => copy.State != OutputState.Ok)
                        .Select(copy => $"{copy.State}: {copy.Output}"))
                    .ToList();
                var convertedBundles = verify.Edits.Select(edit => edit.File)
                    .Concat(verify.Outputs.Select(copy => copy.File)).Distinct(StringComparer.Ordinal).Count();
                Log($"Converted bundles: {convertedBundles}. Applied edits: {verify.Count(EditState.Applied)}. " +
                    $"Verified copies: {verify.Count(OutputState.Ok)}. Problems: {problems.Count}.");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(verify.IsComplete, Is.True, string.Join("\n", problems.Take(50)));
                    Assert.That(convertedBundles, Is.GreaterThan(0), "An empty journal does not prove bundle conversion.");
                    Assert.That(conversionFailures, Is.Empty, string.Join("\n", conversionFailures));
                }
                return Task.CompletedTask;
            }, Log);
        }
        catch (Exception exception)
        {
            // Still uninstall and report the round trip when installation or its assertions fail.
            installFailure = exception;
            Log($"Install/verification failed: {exception}");
        }

        await TimeAsync("5. Uninstall", async () =>
        {
            if (fileSystem.File.Exists(cachePath))
            {
                await UninstallAsync();
            }
            else
            {
                Assert.That(installFailure, Is.Not.Null, "A successful install must leave uninstall.zip.");
                Log("No uninstall.zip after the failed install. Comparing the remaining folder.");
            }
        }, Log);

        var after = Snapshot(fileSystem, install, "6. Restored snapshot", Log);
        WriteSnapshot(fileSystem, report, "restored-snapshot.json", after);
        var differences = Compare(before, after);
        var totals = $"Before: {before.Files.Count} files, {before.TotalBytes} bytes. " +
                     $"After: {after.Files.Count} files, {after.TotalBytes} bytes. " +
                     $"Differences: {differences.Count} " +
                     $"(added {differences.Count(item => item.Kind == "added")}, " +
                     $"removed {differences.Count(item => item.Kind == "removed")}, " +
                     $"changed {differences.Count(item => item.Kind == "changed")}).";
        Log(totals);
        if (report is not null)
        {
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(report, "diff.json"), JsonSerializer.Serialize(
                new { Totals = totals, Differences = differences }, new JsonSerializerOptions { WriteIndented = true }));
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(report, "diff.txt"),
                totals + "\n" + string.Join("\n", differences.Select(item => $"{item.Kind}: {item.Path}")) + "\n");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(installFailure, Is.Null, $"Install/verification failed: {installFailure}");
            Assert.That(differences, Is.Empty,
                totals + "\n" + string.Join("\n", differences.Take(50).Select(item => $"{item.Kind}: {item.Path}")));
            Assert.That(after.TotalBytes, Is.EqualTo(before.TotalBytes), totals);
        }
    }

    private static async Task TimeAsync(string step, Func<Task> action, Action<string> log)
    {
        var timer = Stopwatch.StartNew();
        log($"{step}: starting.");
        try
        {
            await action();
        }
        finally
        {
            log($"{step}: {timer.Elapsed.TotalSeconds:F2} seconds.");
        }
    }

    private static FileSnapshot Snapshot(IFileSystem fileSystem, string install, string step, Action<string> log)
    {
        var timer = Stopwatch.StartNew();
        log($"{step}: starting.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var path in fileSystem.Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories))
        {
            using var stream = fileSystem.File.OpenRead(path);
            totalBytes += stream.Length;
            files.Add(fileSystem.Path.GetRelativePath(install, path).Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(stream)));
            if (files.Count % 1000 == 0)
            {
                log($"{step}: hashed {files.Count} files, {totalBytes} bytes.");
            }
        }
        log($"{step}: {files.Count} files, {totalBytes} bytes, {timer.Elapsed.TotalSeconds:F2} seconds.");
        return new FileSnapshot(files, totalBytes);
    }

    private static List<FileDifference> Compare(FileSnapshot before, FileSnapshot after)
    {
        var differences = new List<FileDifference>();
        foreach (var path in before.Files.Keys.Union(after.Files.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.Files.TryGetValue(path, out var beforeHash);
            after.Files.TryGetValue(path, out var afterHash);
            if (beforeHash == afterHash)
                continue;

            var kind = beforeHash is null ? "added" : afterHash is null ? "removed" : "changed";
            differences.Add(new FileDifference(kind, path, beforeHash, afterHash));
        }
        return differences;
    }

    private static void WriteSnapshot(IFileSystem fileSystem, string? report, string name, FileSnapshot snapshot)
    {
        if (report is null)
            return;

        fileSystem.Directory.CreateDirectory(report);
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(report, name),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetAllowedPath(IFileSystem fileSystem, string input)
    {
        var path = fileSystem.Path.TrimEndingDirectorySeparator(fileSystem.Path.GetFullPath(input));
        string[] forbidden =
        [
            @"C:\Program Files (x86)\Steam",
            @"D:\KSP2-URP-Test",
            @"C:\Games\KSP Stuff\KSP 2 Modding\ksp2 backups"
        ];
        foreach (var root in forbidden)
        {
            Assert.That(IsWithin(fileSystem, path, root) || IsWithin(fileSystem, root, path), Is.False,
                "This test must not access the protected game or backup folders, or their ancestors.");
        }
        return path;
    }

    private static bool IsWithin(IFileSystem fileSystem, string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(fileSystem.Path.EndsInDirectorySeparator(root) ? root : root + fileSystem.Path.DirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    private sealed record FileSnapshot(SortedDictionary<string, string> Files, long TotalBytes)
    {
        public int FileCount => Files.Count;
    }

    private sealed record FileDifference(string Kind, string Path, string? BeforeSha256, string? AfterSha256);

    private sealed class LocalPatchDownloads : IPatchDownloadService
    {
        public IReadOnlyList<Task<string>> EnqueueAll(IReadOnlyList<PatchDownloadRequest> requests,
            PatchDownloadSource source, int maxConcurrency, Action<string> log, Action<long, long> progress,
            CancellationToken ct)
        {
            if (requests.Count != 0)
                throw new InvalidOperationException("The end-to-end plan must use only the local patch.");

            return [];
        }
    }
}
