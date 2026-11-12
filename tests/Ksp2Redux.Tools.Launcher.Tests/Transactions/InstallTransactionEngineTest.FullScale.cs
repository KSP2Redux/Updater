using System.Diagnostics;
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
    /// Exercise complete stock-sized generations using retained real component outputs and one disposable installation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win"), NonParallelizable, Category("Integration")]
    public async Task FullScaleTransactionProbe_RetainsCompleteStockAndChangedOnlyStorage()
    {
        string? configPath = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_FULL_SCALE_TRANSACTION_PROBE");
        if (string.IsNullOrWhiteSpace(configPath))
        {
            Assert.Ignore("Set the explicit full-scale transaction probe config through the maintained preparation test runner.");
            return;
        }
        var fs = new RealFileSystem();
        var engine = new InstallTransactionEngine(fs);
        string workspace = fs.Path.GetDirectoryName(typeof(InstallTransactionEngineTest).Assembly.Location)!;
        while (!fs.Directory.Exists(fs.Path.Combine(workspace, "Documentation", "Omnipatching")))
        {
            workspace = fs.Path.GetDirectoryName(workspace) ?? throw new IOException("Probe checkout not found.");
        }
        string library = fs.Path.Combine(workspace, "Library", "Omnipatching");
        string reference = fs.Path.Combine(workspace, "reference", "omnipatching");
        string configHash = Hash(configPath);
        using var config = JsonDocument.Parse(fs.File.ReadAllBytes(configPath));
        JsonElement options = config.RootElement;
        string output = options.GetProperty("OutputRoot").GetString()!;
        RequireContained(output, fs.Path.Combine(library, "launcher-preparation"));
        if (!fs.Path.GetFileName(output).StartsWith("fullscale-", StringComparison.Ordinal))
            throw new InvalidDataException("The probe output is not a contained fullscale run.");
        string archiveReceiptPath = options.GetProperty("ArchiveReceiptPath").GetString()!;
        string measurementsPath = options.GetProperty("MeasurementsPath").GetString()!;
        RequireContained(archiveReceiptPath, reference);
        RequireContained(measurementsPath, library);
        RequireHash(archiveReceiptPath, options.GetProperty("ArchiveReceiptSha256").GetString()!);
        RequireHash(measurementsPath, options.GetProperty("MeasurementsSha256").GetString()!);
        using var archiveReceipt = JsonDocument.Parse(fs.File.ReadAllBytes(archiveReceiptPath));
        using var measurements = JsonDocument.Parse(fs.File.ReadAllBytes(measurementsPath));
        JsonElement original = archiveReceipt.RootElement;
        if (original.GetProperty("status").GetString() != "complete" || !measurements.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidDataException("Both retained input measurements must have completed.");
        string originalArchive = original.GetProperty("archivePath").GetString()!;
        string originalArchiveHash = original.GetProperty("archiveSha256").GetString()!;
        string manifestPath = original.GetProperty("expectedManifestPath").GetString()!;
        RequireContained(originalArchive, reference);
        RequireContained(manifestPath, reference);
        RequireHash(manifestPath, original.GetProperty("expectedManifestSha256").GetString()!);
        StockBaselineManifest stock = StockBaselineManifest.FromJson(fs.File.ReadAllBytes(manifestPath));
        var inputs = new List<(string Root, StockBaselineFile File)>();
        foreach (JsonElement measurement in measurements.RootElement.GetProperty("measurements").EnumerateArray())
        {
            JsonElement receipt = measurement.GetProperty("result");
            string path = receipt.GetProperty("path").GetString()!;
            RequireContained(path, library);
            RequireHash(path, receipt.GetProperty("sha256").GetString()!);
            using var result = JsonDocument.Parse(fs.File.ReadAllBytes(path));
            StockBaselineFile file = JsonSerializer.Deserialize<StockBaselineFile>(result.RootElement.GetProperty("ExactStagedFiles")[0].GetRawText())!;
            string root = fs.Path.Combine(fs.Path.GetDirectoryName(path)!, "stage");
            RequireContained(root, library);
            if (stock.Files.Any(existing => existing.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The scale component must be an explicit fixture addition, not an accidental stock replacement.");
            string source = ContractPath(root, file.Path);
            RequireHash(source, file.Sha256);
            Assert.That(fs.FileInfo.New(source).Length, Is.EqualTo(file.Length));
            inputs.Add((root, file));
        }
        if (inputs.Count != 2 || inputs[0].File.Path != inputs[1].File.Path)
            throw new InvalidDataException("The scale fixture requires two retained measurements for the same component path.");
        string install = fs.Path.Combine(output, "game");
        string archive = fs.Path.Combine(install, "uninstall.zip");
        string emptySource = fs.Path.Combine(output, "empty-source");
        if (!fs.Directory.Exists(output))
            fs.Directory.CreateDirectory(output);
        foreach (string path in new[] { output, install, emptySource,
                     fs.Path.GetDirectoryName(originalArchive)!, inputs[0].Root, inputs[1].Root })
        {
            engine.ValidateControlledPath(path);
        }
        string owner = fs.Path.Combine(output, "input.sha256");
        if (fs.File.Exists(owner))
            Assert.That(fs.File.ReadAllText(owner), Is.EqualTo(configHash), "Existing scale run has different pinned inputs.");
        else
        {
            Assert.That(fs.Directory.EnumerateFileSystemEntries(output), Is.Empty, "Existing foreign output is not acquired.");
            fs.File.WriteAllText(owner, configHash);
        }
        using var lease = fs.FileStream.New(fs.Path.Combine(output, ".probe.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.That(lease.Length, Is.Zero);
        fs.Directory.CreateDirectory(emptySource);
        var archiveService = new StockArchive(fs, new ZipFileService(fs));
        var watch = Stopwatch.StartNew();
        long allocated = GC.GetTotalAllocatedBytes();
        var results = new List<object>();
        string stageName = "initialize";
        TestContext.Progress.WriteLine($"Full-scale transaction evidence: {output}");
        TestContext.AddTestAttachment(configPath, "Explicit stock-sized fixture inputs, authority qualifications and resume root.");
        try
        {
            CheckFeedback();
            RequireHash(originalArchive, originalArchiveHash);
            if (!fs.Directory.Exists(install))
            {
                Progress("restore-one-disposable-installation");
                await archiveService.RestoreToNewDirectoryAsync(originalArchive, stock, install);
            }
            if (!fs.File.Exists(archive))
            {
                Progress("copy-one-immutable-stock-archive");
                fs.File.Copy(originalArchive, archive, false);
            }
            RequireHash(archive, originalArchiveHash);
            string userPath = fs.Path.Combine(install, "mods", "fullscale-user.bin");
            byte[] userBytes = Encoding.UTF8.GetBytes("literal unknown full-scale fixture data must survive every generation");
            if (!fs.File.Exists(userPath) && fs.Directory.GetFiles(output, "*.intent.json").Length == 0)
            {
                fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(userPath)!);
                fs.File.WriteAllBytes(userPath, userBytes);
            }
            RequireHash(userPath, Digest(userBytes));
            var archiveFile = new StockBaselineFile(archive, fs.FileInfo.New(archive).Length, originalArchiveHash);
            var targetA = new StockBaselineManifest("scale-fixture-a", "Complete stock plus actual rebuilt component. Not a complete rebuilt game.", stock.Files.Append(inputs[0].File), stock.Directories);
            var targetB = new StockBaselineManifest("scale-fixture-b", "Complete stock plus actual edited component. Not a complete rebuilt game.", stock.Files.Append(inputs[1].File), stock.Directories);
            byte[] damage = Encoding.UTF8.GetBytes("known contained scale-fixture damage");
            var damaged = new StockBaselineManifest("scale-fixture-damage", "Explicit known owned-file damage.", stock.Files.Append(
                new StockBaselineFile(inputs[0].File.Path, damage.Length, Digest(damage))), stock.Directories);
            var phases = new[]
            {
                (Name: "install", Current: stock, Target: targetA, Source: inputs[0].Root, Interrupt: false),
                (Name: "update", Current: targetA, Target: targetB, Source: inputs[1].Root, Interrupt: false),
                (Name: "downgrade", Current: targetB, Target: targetA, Source: inputs[0].Root, Interrupt: false),
                (Name: "repair", Current: damaged, Target: targetA, Source: inputs[0].Root, Interrupt: false),
                (Name: "interrupted-update", Current: targetA, Target: targetB, Source: inputs[1].Root, Interrupt: true),
                (Name: "uninstall", Current: targetA, Target: stock, Source: emptySource, Interrupt: false)
            };
            foreach (var phase in phases)
            {
                CheckFeedback();
                stageName = phase.Name;
                string completedPath = fs.Path.Combine(output, phase.Name + ".complete.json");
                if (fs.File.Exists(completedPath))
                {
                    using var completed = JsonDocument.Parse(fs.File.ReadAllBytes(completedPath));
                    Assert.That(completed.RootElement.GetProperty("InputSha256").GetString(), Is.EqualTo(configHash));
                    results.Add(completed.RootElement.Clone());
                    continue;
                }
                Progress(phase.Name);
                var phaseWatch = Stopwatch.StartNew();
                string intentPath = fs.Path.Combine(output, phase.Name + ".intent.json");
                Guid id;
                bool restarting = fs.File.Exists(intentPath);
                if (restarting)
                {
                    using var intent = JsonDocument.Parse(fs.File.ReadAllBytes(intentPath));
                    Assert.That(intent.RootElement.GetProperty("InputSha256").GetString(), Is.EqualTo(configHash));
                    id = intent.RootElement.GetProperty("TransactionId").GetGuid();
                }
                else
                {
                    id = Guid.NewGuid();
                    Save(intentPath, new { InputSha256 = configHash, TransactionId = id });
                }
                if (phase.Name == "repair" && !restarting)
                {
                    string path = ContractPath(install, inputs[0].File.Path);
                    RequireHash(path, inputs[0].File.Sha256);
                    fs.File.WriteAllBytes(path, damage);
                }
                string work = fs.Path.Combine(output, "transaction-" + id.ToString("N"));
                InstallTransactionPlan MakePlan(Guid identity, string workRoot) => new(identity, install, phase.Source, workRoot, phase.Current, phase.Target, stock, archiveFile,
                    protectedRoots: new[] { fs.Path.GetDirectoryName(originalArchive)!, inputs[0].Root, inputs[1].Root },
                    useChangedFileStaging: true);
                var plan = MakePlan(id, work);
                int componentIndex = phase.Target.Files.ToList().FindIndex(file => file.Path == inputs[0].File.Path);
                bool injected = false;
                long copiedTargetBytes = 0;
                long copiedUndoBytes = 0;
                InstallTransactionResult outcome = default;
                if (restarting)
                {
                    // Independent pinned inputs reconstruct the plan. Disk intent contributes only the operation identity.
                    outcome = engine.Recover(plan);
                    string injection = fs.Path.Combine(output, $"{phase.Name}.injected-{id:N}.json");
                    if (outcome == InstallTransactionResult.RolledBack && (!phase.Interrupt || !fs.File.Exists(injection)))
                    {
                        // Recovery verified the prior generation. Keep the old intent and work before retrying with a new identity.
                        Save(fs.Path.Combine(output, $"{phase.Name}.recovered-{id:N}.json"),
                            new { InputSha256 = configHash, TransactionId = id, Outcome = outcome.ToString(), WorkRoot = work });
                        id = Guid.NewGuid();
                        Save(intentPath, new { InputSha256 = configHash, TransactionId = id });
                        work = fs.Path.Combine(output, "transaction-" + id.ToString("N"));
                        plan = MakePlan(id, work);
                        restarting = false;
                    }
                }
                if (!restarting)
                {
                    var operation = new InstallTransactionEngine(fs, operationBoundary: (name, after) =>
                    {
                        if (after && name.EndsWith(":flush", StringComparison.Ordinal))
                        {
                            string[] parts = name.Split(':')[0].Split('-');
                            if (parts.Length == 2 && int.TryParse(parts[1], out int index) && parts[0] is "stage" or "undo")
                            {
                                string folder = parts[0] == "stage" ? "target" : "undo";
                                long bytes = fs.FileInfo.New(fs.Path.Combine(work, folder, $"{index:D8}")).Length;
                                if (folder == "target")
                                {
                                    copiedTargetBytes += bytes;
                                }
                                else
                                {
                                    copiedUndoBytes += bytes;
                                }
                            }
                        }
                        if (phase.Interrupt && !injected && after && name == $"install-{componentIndex}")
                        {
                            injected = true;
                            Save(fs.Path.Combine(output, $"{phase.Name}.injected-{id:N}.json"),
                                new { InputSha256 = configHash, TransactionId = id, Boundary = name });
                            throw new IOException("Injected native interruption after changed-file publication.");
                        }
                    });
                    if (phase.Interrupt)
                    {
                        Assert.Throws<IOException>(() => operation.Apply(plan));
                        Assert.That(injected, Is.True);
                        outcome = engine.Recover(plan);
                        Assert.That(engine.Recover(plan), Is.EqualTo(outcome));
                    }
                    else
                    {
                        outcome = operation.Apply(plan);
                    }
                }
                Assert.That(outcome, Is.EqualTo(phase.Interrupt ? InstallTransactionResult.RolledBack : InstallTransactionResult.Committed));
                var currentFiles = phase.Current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var targetFiles = phase.Target.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                long expectedTargetBytes = phase.Target.Files.Where(file => currentFiles.GetValueOrDefault(file.Path) != file).Sum(file => file.Length);
                long expectedUndoBytes = phase.Current.Files.Where(file => targetFiles.GetValueOrDefault(file.Path) != file).Sum(file => file.Length);
                if (!restarting)
                {
                    Assert.That(copiedTargetBytes, Is.EqualTo(expectedTargetBytes));
                    Assert.That(copiedUndoBytes, Is.EqualTo(expectedUndoBytes));
                }
                var storage = new Dictionary<string, object>();
                foreach (string name in new[] { "target", "undo", "removed", "rollback" })
                {
                    string path = fs.Path.Combine(work, name);
                    string[] files = fs.Directory.Exists(path) ? fs.Directory.GetFiles(path) : [];
                    storage[name] = new { Files = files.Length, Bytes = files.Sum(file => fs.FileInfo.New(file).Length) };
                }
                var row = new
                {
                    InputSha256 = configHash, Phase = phase.Name, TransactionId = id, Outcome = outcome.ToString(),
                    CurrentFiles = phase.Current.Files.Count, TargetFiles = phase.Target.Files.Count,
                    CurrentBytes = phase.Current.TotalBytes, TargetBytes = phase.Target.TotalBytes,
                    ExpectedTargetStageBytes = expectedTargetBytes, ExpectedUndoBytes = expectedUndoBytes,
                    CopiedTargetBytes = copiedTargetBytes, CopiedUndoBytes = copiedUndoBytes, RecoveredExistingIntent = restarting,
                    AvoidedTargetAndUndoBytes = phase.Target.TotalBytes + phase.Current.TotalBytes - expectedTargetBytes - expectedUndoBytes,
                    Storage = storage, ElapsedMilliseconds = phaseWatch.Elapsed.TotalMilliseconds
                };
                Save(completedPath, row);
                results.Add(row);
            }
            RequireHash(archive, originalArchiveHash);
            RequireHash(originalArchive, originalArchiveHash);
            Progress("independent-final-stock-hashes");
            foreach (StockBaselineFile file in stock.Files)
            {
                string path = ContractPath(install, file.Path);
                RequireHash(path, file.Sha256);
                Assert.That(fs.FileInfo.New(path).Length, Is.EqualTo(file.Length), file.Path);
            }
            foreach (string directory in stock.Directories)
            {
                Assert.That(fs.Directory.Exists(ContractPath(install, directory)), Is.True, directory);
            }
            Assert.That(fs.File.Exists(ContractPath(install, inputs[0].File.Path)), Is.False);
            RequireHash(userPath, Digest(userBytes));
            foreach (var input in inputs)
            {
                RequireHash(ContractPath(input.Root, input.File.Path), input.File.Sha256);
            }
            RequireHash(manifestPath, original.GetProperty("expectedManifestSha256").GetString()!);
            string report = fs.Path.Combine(output, "result.json");
            Save(report, new
            {
                Success = true, ExplicitFixtureAuthority = true, FullRebuiltGame = false, AuthenticatedDelivery = false,
                NativeHost = "Windows", PosixHostVerified = false, GameOrUnityControlled = false,
                StockFiles = stock.Files.Count, StockBytes = stock.TotalBytes, StockManifestSha256 = Hash(manifestPath),
                IndependentRestoredStockHashesVerified = true, UnknownUserDataPreserved = true,
                OriginalArchiveSha256 = originalArchiveHash, ImmutableInstalledArchiveSha256 = Hash(archive),
                InputSha256 = configHash, Phases = results, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                ReusedCompletedPhaseRecords = results.Count(row => row is JsonElement),
                RecordedPhaseElapsedMilliseconds = results.Sum(row => JsonSerializer.SerializeToElement(row).GetProperty("ElapsedMilliseconds").GetDouble()),
                ResourceMeasurementsCoverCurrentAttemptOnly = true,
                CumulativeManagedAllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                ProcessLifetimePeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                ManagedAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                PagingAllowed = true, FreeDiskBytes = new DriveInfo(fs.Path.GetPathRoot(output)!).AvailableFreeSpace
            });
            TestContext.AddTestAttachment(report, "Actual complete-stock-sized transaction and changed-file storage results. Fixture authority only.");
        }
        catch (Exception failure)
        {
            Save(fs.Path.Combine(output, $"failure-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.json"),
                new { Phase = stageName, Type = failure.GetType().FullName, failure.Message, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
            throw;
        }

        string Hash(string path)
        {
            engine.ValidateControlledPath(path);
            using var stream = fs.File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        void RequireHash(string path, string expected) => Assert.That(Hash(path), Is.EqualTo(expected.ToUpperInvariant()), path);

        string ContractPath(string root, string relative) => fs.Path.Combine(root, relative.Replace('/', fs.Path.DirectorySeparatorChar));

        void RequireContained(string path, string parent)
        {
            engine.ValidateControlledPath(path);
            if (!path.StartsWith(parent + fs.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Scale probe path leaves the isolated checkout evidence root.");
        }

        void Save(string path, object value) => fs.File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));

        void CheckFeedback()
        {
            string path = options.GetProperty("ParentFeedbackPath").GetString()!;
            string? expected = options.GetProperty("ParentFeedbackSha256").GetString();
            string? actual = fs.File.Exists(path) ? Hash(path) : null;
            if (actual != expected)
                throw new IOException("Parent feedback changed. Read it before starting another scale phase. Existing evidence is retained.");
        }

        void Progress(string phase)
        {
            Save(fs.Path.Combine(output, "progress.json"), new
            {
                Phase = phase, Utc = DateTime.UtcNow, ProcessId = SystemEnvironmentProvider.Instance.ProcessId,
                ProcessWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
                ManagedAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, PagingAllowed = true
            });
            TestContext.Progress.WriteLine($"{DateTime.UtcNow:O} full-scale {phase}");
        }
    }
}
