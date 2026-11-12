using System.Diagnostics;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Tests;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Opt-in native filesystem release preparation over independently frozen local fixture contracts.
/// </summary>
public class ReleaseRecipePipelineProbe
{
    /// <summary>
    /// Reject malformed and production envelopes before any native payload access or output creation.
    /// </summary>
    /// <param name="json">The invalid local binding envelope.</param>
    /// <param name="reason">The expected refusal reason.</param>
    [TestCase("{\"schema\":\"foreign\",\"mode\":\"diagnostic-only-full-player-v1\"}", "Only explicit diagnostic-only")]
    [TestCase("{\"schema\":\"omnipatching.full-player-probe-binding.v1\",\"mode\":\"production-full-player-v1\"}", "Only explicit diagnostic-only")]
    [TestCase("{\"schema\":\"first\",\"schema\":\"second\"}", "Duplicate binding JSON property")]
    [TestCase("{\"schema\":\"omnipatching.full-player-probe-binding.v1\",\"unconsumedTargetRoot\":\"ignored\"}", "Unknown full-player binding field")]
    public void FullPlayerBindingRefusesMalformedOrProductionEnvelope(string json, string reason)
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\binding-negative";
        string path = fs.Path.Combine(root, "binding.json");
        fs.Directory.CreateDirectory(root);
        fs.File.WriteAllText(path, json);
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => FullPlayerProbeBinding.Read(fs, path))!;
        Assert.That(failure.Message, Does.Contain(reason));
        Assert.That(fs.Directory.EnumerateFileSystemEntries(root).Count(), Is.EqualTo(1));
    }

    /// <summary>
    /// Refuse identity, debug and publication controls as the changed runtime witness.
    /// </summary>
    /// <param name="changedPath">The control-only difference in two small mock contracts.</param>
    [TestCase("KSP2_x64_Data/app.info")]
    [TestCase("KSP2_x64_Data/boot.config")]
    [TestCase("KSP2_x64_Data/Managed/Ksp2Redux.pdb")]
    [TestCase("KSP2_x64_Data/Managed/Ksp2Redux.dll.mdb")]
    [TestCase("KSP2_x64_Data/ScriptingAssemblies.json")]
    [TestCase("KSP2_x64_Data/StreamingAssets/aa/catalog.json")]
    [TestCase("KSP2_x64_Data/StreamingAssets/aa/catalog.hash")]
    [TestCase("KSP2_x64_Data/StreamingAssets/aa/settings.json")]
    [TestCase("KSP2_x64_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/PluginInfo.xml")]
    [TestCase("KSP2_x64_Data/StreamingAssets/control.bundle")]
    [TestCase("Redux/Addressables/StandaloneWindows64/catalog.json")]
    [TestCase("source-player-diagnostic.json")]
    public void FullPlayerBindingRefusesControlOnlyWitness(string changedPath)
    {
        var mock = CreateMockPlayerPair(changedPath);
        FullPlayerProbeBinding first = FullPlayerProbeBinding.Read(mock.Fs, mock.FirstPath);
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => first.ReadSuccessor())!;
        Assert.That(failure.Message, Does.Contain("Identity, debug and control files are refused"));
        Assert.That(mock.Fs.Directory.Exists(first.PublicationRoot), Is.False);
    }

    /// <summary>
    /// Allow either publication order and the fresh fixture after a changed mock runtime payload.
    /// </summary>
    /// <param name="changedPath">The runtime category represented by the small mock contracts.</param>
    /// <param name="publishNextFirst">True to validate B publication before A, false for A before B.</param>
    [TestCase("Redux/Addressables/StandaloneWindows64/mock.bundle", false)]
    [TestCase("Redux/Addressables/StandaloneWindows64/mock.bundle", true)]
    [TestCase("KSP2_x64_Data/Managed/Ksp2Redux.dll", false)]
    [TestCase("KSP2_x64_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/mock.bnk", false)]
    [TestCase("KSP2_x64_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/123.wem", false)]
    public void FullPlayerBindingAcceptsRuntimeWitnessAndAcyclicPair(string changedPath, bool publishNextFirst)
    {
        var mock = CreateMockPlayerPair(changedPath);
        FullPlayerProbeBinding first = FullPlayerProbeBinding.Read(mock.Fs, mock.FirstPath);
        FullPlayerProbeBinding next = first.ReadSuccessor();
        foreach (FullPlayerProbeBinding selected in publishNextFirst ? new[] { next, first } : new[] { first, next })
        {
            selected.ValidateOutput(mock.Checkout, true);
            mock.Fs.Directory.CreateDirectory(selected.PublicationRoot);
        }
        first.ValidatePairOutput(mock.Checkout, next);
        Assert.That(mock.Fs.Directory.Exists(first.FixtureRoot), Is.False);
    }

    /// <summary>
    /// Refuse cross-generation output intrusion before the publication entry's first directory creation.
    /// </summary>
    /// <param name="intrusion">The pair path conflict in the mock filesystem.</param>
    [TestCase("a-in-b-target")]
    [TestCase("b-in-a-target")]
    [TestCase("a-covers-b-control")]
    [TestCase("a-covers-bindings")]
    [TestCase("fixture-in-b-protected")]
    [TestCase("publication-in-stock")]
    [TestCase("publication-covers-archive")]
    [TestCase("outputs-overlap")]
    [TestCase("case-alias")]
    [TestCase("traversal-alias")]
    public void FullPlayerBindingRefusesPairIntrusionBeforeOutputCreation(string intrusion)
    {
        var mock = CreateMockPlayerPair("Redux/Addressables/StandaloneWindows64/mock.bundle", (first, next) =>
        {
            string library = @"C:\mock-checkout\Library\Omnipatching";
            switch (intrusion)
            {
                case "a-in-b-target": first["publicationRoot"] = next["targetRoot"] + @"\publish"; break;
                case "b-in-a-target": next["publicationRoot"] = first["targetRoot"] + @"\publish"; break;
                case "a-covers-b-control": first["publicationRoot"] = library + @"\mock-controls-b"; break;
                case "a-covers-bindings": first["publicationRoot"] = library + @"\mock-bindings"; break;
                case "fixture-in-b-protected":
                    first["fixtureRoot"] = next["fixtureRoot"] = library + @"\mock-protected-b\fixture";
                    break;
                case "publication-in-stock": first["publicationRoot"] = first["stockRoot"] + @"\publish"; break;
                case "publication-covers-archive": next["publicationRoot"] = library + @"\mock-archive"; break;
                case "outputs-overlap": next["publicationRoot"] = first["publicationRoot"] + @"\nested"; break;
                case "case-alias": first["publicationRoot"] = library + @"\MOCK-INPUT-B\publish"; break;
                case "traversal-alias": first["publicationRoot"] = library + @"\unused\..\mock-input-b\publish"; break;
                default: throw new InvalidDataException("Unknown mock intrusion.");
            }
        });
        string[] before = mock.Fs.Directory.EnumerateFileSystemEntries(mock.Checkout, "*", SearchOption.AllDirectories).Order().ToArray();
        foreach (string path in new[] { mock.FirstPath, mock.NextPath })
        {
            Assert.Throws<InvalidDataException>(() =>
            {
                FullPlayerProbeBinding binding = FullPlayerProbeBinding.Read(mock.Fs, path);
                binding.ValidateOutput(mock.Checkout, true);
                mock.Fs.Directory.CreateDirectory(binding.PublicationRoot);
            });
        }
        Assert.Throws<InvalidDataException>(() =>
        {
            FullPlayerProbeBinding first = FullPlayerProbeBinding.Read(mock.Fs, mock.FirstPath);
            first.ValidatePairOutput(mock.Checkout, first.ReadSuccessor());
            mock.Fs.Directory.CreateDirectory(first.FixtureRoot);
        });
        Assert.That(mock.Fs.Directory.EnumerateFileSystemEntries(mock.Checkout, "*", SearchOption.AllDirectories).Order(), Is.EqualTo(before));
    }

    /// <summary>
    /// Refuse absent, malformed, changed, cyclic or foreign counterpart controls without output creation.
    /// </summary>
    /// <param name="fault">The broken pair authority.</param>
    /// <param name="exceptionType">The specific expected failure type.</param>
    [TestCase("missing", typeof(FileNotFoundException))]
    [TestCase("malformed", typeof(JsonException))]
    [TestCase("pin-mismatch", typeof(InvalidDataException))]
    [TestCase("cycle", typeof(InvalidDataException))]
    [TestCase("foreign-parent", typeof(InvalidDataException))]
    [TestCase("missing-parent", typeof(FileNotFoundException))]
    [TestCase("malformed-parent", typeof(JsonException))]
    [TestCase("parent-pin-mismatch", typeof(InvalidDataException))]
    public void FullPlayerBindingRefusesBrokenCounterpart(string fault, Type exceptionType)
    {
        var mock = CreateMockPlayerPair("Redux/Addressables/StandaloneWindows64/mock.bundle", (first, next) =>
        {
            if (fault == "cycle")
                next["successorBinding"] = new { path = @"C:\mock-checkout\Library\Omnipatching\mock-bindings\a.json", sha256 = MockDigest("mock cycle") };
            if (fault == "foreign-parent")
                next["parentBinding"] = @"C:\mock-foreign\a.json";
        });
        if (fault == "missing")
            mock.Fs.File.Delete(mock.NextPath);
        if (fault == "malformed")
        {
            mock.Fs.File.WriteAllText(mock.NextPath, "{");
            var first = JsonSerializer.Deserialize<Dictionary<string, object?>>(mock.Fs.File.ReadAllText(mock.FirstPath))!;
            first["successorBinding"] = new { path = mock.NextPath, sha256 = MockDigest("{") };
            mock.Fs.File.WriteAllBytes(mock.FirstPath, JsonSerializer.SerializeToUtf8Bytes(first));
        }
        if (fault is "pin-mismatch" or "parent-pin-mismatch")
            mock.Fs.File.AppendAllText(mock.NextPath, " ");
        if (fault == "missing-parent")
            mock.Fs.File.Delete(mock.FirstPath);
        if (fault == "malformed-parent")
            mock.Fs.File.WriteAllText(mock.FirstPath, "{");
        FullPlayerProbeBinding binding = FullPlayerProbeBinding.Read(mock.Fs,
            fault is "missing-parent" or "malformed-parent" or "parent-pin-mismatch"
                ? mock.NextPath : mock.FirstPath);
        Exception failure = Assert.Catch(() =>
        {
            binding.ValidateOutput(mock.Checkout, true);
            mock.Fs.Directory.CreateDirectory(binding.PublicationRoot);
        })!;
        Assert.That(failure, Is.InstanceOf(exceptionType));
        Assert.That(mock.Fs.Directory.Exists(binding.PublicationRoot), Is.False);
        Assert.That(mock.Fs.Directory.Exists(binding.FixtureRoot), Is.False);
    }

    private static (MockFileSystem Fs, string FirstPath, string NextPath, string Checkout) CreateMockPlayerPair(string changedPath,
        Action<Dictionary<string, object?>, Dictionary<string, object?>>? configure = null)
    {
        // These tiny controls exercise binding logic only. They are not built players or archive qualification.
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string checkout = @"C:\mock-checkout";
        const string library = checkout + @"\Library\Omnipatching";
        const string firstPath = library + @"\mock-bindings\a.json";
        const string nextPath = library + @"\mock-bindings\b.json";
        string archivePath = library + @"\mock-archive\uninstall.zip";
        string archiveHash = MockDigest("mock archive, never a real ZIP");
        var stock = new StockBaselineManifest("small-mock-stock", "Binding-only mock, not stock qualification.",
            new[] { new StockBaselineFile("stock.txt", 1, MockDigest("s")) });
        object WritePin(string path, byte[] bytes)
        {
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
            fs.File.WriteAllBytes(path, bytes);
            return new { path, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        byte[] stockBytes = stock.ToJson();
        object stockPin = WritePin(library + @"\mock-controls-stock\manifest.json", stockBytes);
        byte[] generation = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "complete", expectedManifestSha256 = Convert.ToHexString(SHA256.HashData(stockBytes)),
            expectedFileCount = stock.Files.Count, expectedDirectoryCount = stock.Directories.Count, expectedBytes = stock.TotalBytes,
            archiveSha256 = archiveHash, archivePath = @"C:\mock-original\uninstall.zip", archiveLength = 1
        });
        object generationPin = WritePin(library + @"\mock-controls-stock\generation.json", generation);
        object relocationPin = WritePin(library + @"\mock-controls-stock\relocation.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "omnipatching.stock-archive-relocation.v1", state = "complete", source = @"C:\mock-original\uninstall.zip",
            destination = archivePath, generationSha256 = Convert.ToHexString(SHA256.HashData(generation)),
            expectedSha256 = archiveHash, expectedBytes = 1
        }));
        Dictionary<string, object?> MakeBinding(string label)
        {
            string targetRoot = library + @"\mock-input-" + label;
            string contractHash = MockDigest("mock source contract " + label);
            byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "omnipatching.source-player-diagnostic-build.v1", success = true, sourceOwnedPlayerBuilt = true,
                facadesAbsentFromOutput = true, platform = "StandaloneWindows64", finalBuildReady = false,
                releaseEligible = false, fullGoalComplete = false, contractSha256 = contractHash
            });
            object receiptPin = WritePin(targetRoot + @"\source-player-diagnostic.json", receipt);
            string[] paths = { "KSP2_x64.exe", "KSP2_x64_Data/boot.config", "KSP2_x64_Data/StreamingAssets/aa/catalog.json",
                "Redux/Addressables/StandaloneWindows64/mock.bundle", changedPath };
            var files = paths.Distinct(StringComparer.Ordinal).Select(path =>
                new StockBaselineFile(path, 1, MockDigest(path == changedPath ? "mock changed " + label : "mock same"))).ToList();
            if (changedPath != "source-player-diagnostic.json")
                files.Add(new StockBaselineFile("source-player-diagnostic.json", receipt.Length, Convert.ToHexString(SHA256.HashData(receipt))));
            var target = new StockBaselineManifest("small-mock-" + label, "Binding-only mock, not player qualification.", files);
            return new Dictionary<string, object?>
            {
                ["schema"] = "omnipatching.full-player-probe-binding.v1", ["mode"] = "diagnostic-only-full-player-v1",
                ["publicationRoot"] = library + @"\mock-publication-" + label, ["fixtureRoot"] = library + @"\mock-fixture",
                ["targetRoot"] = targetRoot, ["stockRoot"] = library + @"\mock-stock", ["stockManifest"] = stockPin,
                ["targetManifest"] = WritePin(library + @"\mock-controls-" + label + @"\target.json", target.ToJson()),
                ["playerReceipt"] = receiptPin, ["sourceContractSha256"] = contractHash,
                ["stockArchive"] = new { path = archivePath, sha256 = archiveHash, length = 1 },
                ["stockGeneration"] = generationPin, ["stockRelocation"] = relocationPin,
                ["expectedTargetIdentity"] = target.BaselineIdentity, ["releaseId"] = "small-mock-" + label,
                ["channel"] = "beta", ["version"] = "1.0.0", ["sources"] = Array.Empty<ReleaseRecipeSource>(),
                ["protectedRoots"] = new[] { library + @"\mock-protected-" + label },
                ["successorBinding"] = null, ["parentBinding"] = label == "a" ? null : firstPath,
                ["changedPayloadPath"] = changedPath
            };
        }
        Dictionary<string, object?> first = MakeBinding("a");
        Dictionary<string, object?> next = MakeBinding("b");
        configure?.Invoke(first, next);
        first["successorBinding"] = WritePin(nextPath, JsonSerializer.SerializeToUtf8Bytes(next));
        _ = WritePin(firstPath, JsonSerializer.SerializeToUtf8Bytes(first));
        return (fs, firstPath, nextPath, checkout);
    }

    private static string MockDigest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Publish and stage configured frozen inputs, recording exact output and process resource evidence.
    /// </summary>
    [Test]
    [Category("Integration")]
    public void NativeFrozenReleasePublishesAndStages()
    {
        string? configPath = System.Environment.GetEnvironmentVariable("KSP2REDUX_RELEASE_RECIPE_PROBE");
        string? bindingPath = System.Environment.GetEnvironmentVariable("KSP2REDUX_FULL_PLAYER_PROBE_BINDING");
        if (string.IsNullOrEmpty(configPath) && string.IsNullOrEmpty(bindingPath))
            Assert.Ignore("Set KSP2REDUX_RELEASE_RECIPE_PROBE to a frozen fixture configuration.");
        if (!string.IsNullOrEmpty(configPath) && !string.IsNullOrEmpty(bindingPath))
            throw new InvalidDataException("Select component mode or an explicit full-player binding.");
        IFileSystem fileSystem = new RealFileSystem();
        FullPlayerProbeBinding? fullPlayer = string.IsNullOrEmpty(bindingPath) ? null : FullPlayerProbeBinding.Read(fileSystem, bindingPath);
        var config = fullPlayer is null ? JsonSerializer.Deserialize<ProbeConfig>(fileSystem.File.ReadAllText(configPath!))! :
            new ProbeConfig(fullPlayer.PublicationRoot, fullPlayer.Sources.ToList(), new List<string>());
        string scratch = fileSystem.Path.GetFullPath(config.ScratchRoot);
        string? checkout = fileSystem.Path.GetFullPath(TestContext.CurrentContext.TestDirectory);
        while (checkout is not null && !fileSystem.File.Exists(fileSystem.Path.Combine(checkout, "Documentation", "Omnipatching", "README.md")))
        {
            checkout = fileSystem.Path.GetDirectoryName(checkout);
        }
        if (checkout is null || !scratch.StartsWith(fileSystem.Path.Combine(checkout, "Library", "Omnipatching") +
                fileSystem.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native probe scratch must be below this checkout's Library/Omnipatching.");
        string targetRoot = fullPlayer?.TargetRoot ?? fileSystem.Path.Combine(scratch, "frozen-target");
        string baselineRoot = fullPlayer?.StockRoot ?? fileSystem.Path.Combine(scratch, "frozen-baseline");
        string artifactRoot = fileSystem.Path.Combine(scratch, "artifact");
        string stageRoot = fileSystem.Path.Combine(scratch, "stage");
        var target = fullPlayer?.Target ?? StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(fileSystem.File.ReadAllText(fileSystem.Path.Combine(scratch, "target.json"))));
        var baseline = fullPlayer?.Stock ?? StockBaselineManifest.FromJson(Encoding.UTF8.GetBytes(fileSystem.File.ReadAllText(fileSystem.Path.Combine(scratch, "baseline.json"))));
        if (fullPlayer is not null)
        {
            fullPlayer.ValidateOutput(checkout, true);
            fullPlayer.VerifyInputs();
            fileSystem.Directory.CreateDirectory(scratch);
        }
        var pipeline = new ReleaseRecipePipeline(fileSystem);
        using var process = Process.GetCurrentProcess();
        using var stop = new CancellationTokenSource();
        long peakManaged = GC.GetTotalMemory(false);
        long peakPrivate = 0;
        long peakWorking = 0;
        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                await Task.Delay(10);
            }
        });
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        ReleaseRecipePublication publication;
        double publicationMilliseconds;
        try
        {
            publication = pipeline.Publish(targetRoot, target,
                [new ReleaseRecipeSourceTree(baselineRoot, baseline, config.Sources)], artifactRoot,
                config.CanonicalPaths.ToHashSet(StringComparer.Ordinal));
            publicationMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (fullPlayer is null)
                pipeline.Prepare(artifactRoot, publication.ArtifactSha256, publication.TargetManifestSha256,
                    baselineRoot, baseline, stageRoot);
        }
        finally
        {
            stop.Cancel();
            monitor.GetAwaiter().GetResult();
        }
        double totalMilliseconds = timer.Elapsed.TotalMilliseconds;
        var exact = new List<StockBaselineFile>();
        foreach (var file in fullPlayer is null ? target.Files : Array.Empty<StockBaselineFile>())
        {
            using Stream input = fileSystem.File.OpenRead(fileSystem.Path.Combine(stageRoot, file.Path));
            string hash = Convert.ToHexString(SHA256.HashData(input));
            Assert.That(hash, Is.EqualTo(file.Sha256));
            Assert.That(input.Length, Is.EqualTo(file.Length));
            exact.Add(new StockBaselineFile(file.Path, input.Length, hash));
        }
        var report = new
        {
            LocalIntegrityOnly = true,
            BindingSha256 = fullPlayer?.BindingSha256,
            DiagnosticOnly = fullPlayer is not null,
            ReleaseEligible = false,
            FinalBuildReady = false,
            FullGoalComplete = false,
            ReleaseAuthenticationVerified = false,
            NativeFilesystemStagingVerified = fullPlayer is null,
            UnityCompilationOrGameplayVerified = false,
            TargetBytes = target.TotalBytes,
            BaselineBytes = baseline.TotalBytes,
            Publication = publication,
            PublicationMilliseconds = publicationMilliseconds,
            PreparationMilliseconds = totalMilliseconds - publicationMilliseconds,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            SampledPeakManagedBytes = peakManaged,
            SampledPeakPrivateBytes = peakPrivate,
            SampledPeakWorkingSetBytes = peakWorking,
            ProcessLifetimePeakWorkingSetBytes = process.PeakWorkingSet64,
            SamplingIntervalMilliseconds = 10,
            ExactStagedFiles = exact,
        };
        fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(scratch, "result.json"),
            JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.Out.WriteLine(JsonSerializer.Serialize(new { Result = fileSystem.Path.Combine(scratch, "result.json"),
            TargetFileCount = target.Files.Count, target.TotalBytes, publication.StoredPackBytes,
            BindingSha256 = fullPlayer?.BindingSha256, DiagnosticOnly = fullPlayer is not null, ReleaseEligible = false }));
    }

    private sealed record ProbeConfig(string ScratchRoot, List<ReleaseRecipeSource> Sources, List<string> CanonicalPaths);
}
