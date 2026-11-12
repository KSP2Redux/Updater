using System.IO.Abstractions;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// A standalone configured probe over copied source files and a bundle or exact raw target.
/// </summary>
public class BundleRecipeGeneratorProbe
{
    private const string CONFIG_VARIABLE = "KSP2REDUX_BUNDLE_RECIPE_PROBE";

    /// <summary>
    /// Generates persisted artifacts and reconstructs the exact canonical target from configured copies.
    /// </summary>
    [Test]
    [Category("Integration")]
    public void GenerateAndReconstructConfiguredCopiedBundles()
    {
        string? configPath = System.Environment.GetEnvironmentVariable(CONFIG_VARIABLE);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            Assert.Ignore($"Set {CONFIG_VARIABLE} to a probe JSON file. See BundleRecipeGeneratorProbe.md.");
        }
        IFileSystem fileSystem = new RealFileSystem();
        var config = JsonCodec.Read<ProbeConfig>(fileSystem.File.ReadAllBytes(configPath!))
            ?? throw new InvalidDataException("The probe configuration is null.");
        string? root = fileSystem.Path.GetFullPath(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !fileSystem.File.Exists(fileSystem.Path.Combine(root, "Documentation", "Omnipatching", "README.md")))
        {
            root = fileSystem.Path.GetDirectoryName(root);
        }
        if (root is null)
            throw new InvalidOperationException("The probe must run from this Redux checkout's standalone test project.");
        if (config.TargetKind == "RawFile")
        {
            RequireContainedPath(fileSystem, root, configPath!);
        }
        RunProbe(fileSystem, root, config);
    }

    /// <summary>
    /// Verifies that the standalone probe persists a usable recipe and exact reconstruction in local scratch.
    /// </summary>
    [Test]
    public void ProbeArtifactsReconstructSyntheticCopies()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\ReduxProbe";
        byte[] data = Enumerable.Range(0, 200_000).Select(index => (byte)(index * 17)).ToArray();
        fileSystem.Directory.CreateDirectory(root);
        fileSystem.File.WriteAllBytes(root + @"\source.bin", data);
        fileSystem.File.WriteAllBytes(root + @"\target.bundle", SyntheticBundles.Bundle(false, ("data.resS", data, false)));
        var config = new ProbeConfig
        {
            Sources = [new ProbeSource { Path = root + @"\source.bin", Kind = "RawFile" }],
            Target = root + @"\target.bundle",
            OutputDirectory = root + @"\reference\omnipatching\probe",
        };

        var persistedConfig = JsonCodec.Read<ProbeConfig>(JsonCodec.Write(config))!;
        RunProbe(fileSystem, root, persistedConfig);

        byte[] canonical = fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\canonical.bundle");
        byte[] reconstructed = fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\reconstructed.bundle");
        Assert.That(reconstructed, Is.EqualTo(canonical));
        Assert.That(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\literals.pack"), Is.Empty);
        Assert.That(JsonCodec.Read<BundleRecipe>(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\recipe.json"))!.Sources, Has.Count.EqualTo(1));
        Assert.That(fileSystem.File.ReadAllText(config.OutputDirectory + @"\generation.json"), Does.Contain("ReusedBytes").And.Contain("NativeTargetSha256"));
    }

    /// <summary>
    /// Rejects probe output locations outside the checkout's ignored scratch roots.
    /// </summary>
    [Test]
    public void ProbeRejectsOutputOutsideIgnoredScratch()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        var config = new ProbeConfig { OutputDirectory = @"C:\ReduxProbe\ksp2redux.tools\outputs" };

        Assert.Throws<ArgumentException>(() => RunProbe(fileSystem, @"C:\ReduxProbe", config));

        Assert.That(fileSystem.Directory.Exists(config.OutputDirectory), Is.False);
    }

    /// <summary>
    /// Persists an exact raw recipe and compares reconstructed bytes to the independent configured target.
    /// </summary>
    [Test]
    public void RawProbeArtifactsEqualIndependentTarget()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\ReduxRawProbe";
        const string inputs = root + @"\reference\omnipatching\inputs";
        byte[] media = Enumerable.Range(0, 200_000).Select(index => (byte)(index * 17)).ToArray();
        byte[] expected = new byte[] { 0x52, 0x49, 0x46, 0x46 }.Concat(media).Concat(new byte[] { 13, 10, 0 }).ToArray();
        fileSystem.Directory.CreateDirectory(inputs);
        fileSystem.File.WriteAllBytes(inputs + @"\source.wem", media);
        fileSystem.File.WriteAllBytes(inputs + @"\target.wem", expected);
        var config = new ProbeConfig
        {
            TargetKind = "RawFile",
            Sources = [new ProbeSource { Path = inputs + @"\source.wem", Kind = "RawFile" }],
            Target = inputs + @"\target.wem",
            OutputDirectory = root + @"\Library\Omnipatching\raw-probe",
            RangeHints = [new BundleRecipeRangeHint { TargetEntryIndex = 0, TargetOffset = 4,
                SourceIndex = 0, Length = media.Length, Sha256 = Hex.Sha256(media) }],
        };

        RunProbe(fileSystem, root, JsonCodec.Read<ProbeConfig>(JsonCodec.Write(config))!);

        Assert.That(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\reconstructed.file"), Is.EqualTo(expected));
        Assert.That(fileSystem.File.Exists(config.OutputDirectory + @"\canonical.bundle"), Is.False);
        Assert.That(fileSystem.File.ReadAllBytes(inputs + @"\source.wem"), Is.EqualTo(media));
        using var evidence = System.Text.Json.JsonDocument.Parse(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\generation.json"));
        Assert.That(evidence.RootElement.GetProperty("ReconstructedSha256").GetString(), Is.EqualTo(Hex.Sha256(expected)));
        Assert.That(evidence.RootElement.GetProperty("ByteComparisonVerified").GetBoolean(), Is.True);
        Assert.That(evidence.RootElement.GetProperty("RecipeVersion").GetInt32(), Is.EqualTo(2));
    }

    /// <summary>
    /// Rejects raw probe inputs outside scratch, disguised traversal and junctions before creating artifacts.
    /// </summary>
    /// <param name="invalidPath">The unsafe input form.</param>
    [TestCase("source")]
    [TestCase("target")]
    [TestCase("traversal")]
    [TestCase("prefix")]
    [TestCase("junction")]
    public void RawProbeRejectsUncontainedInputs(string invalidPath)
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\ReduxRawProbe";
        const string inputs = root + @"\reference\omnipatching\inputs";
        fileSystem.Directory.CreateDirectory(inputs);
        fileSystem.File.WriteAllBytes(inputs + @"\source", new byte[] { 1 });
        fileSystem.File.WriteAllBytes(inputs + @"\target", new byte[] { 1 });
        var config = new ProbeConfig
        {
            TargetKind = "RawFile",
            Sources = [new ProbeSource { Path = inputs + @"\source", Kind = "RawFile" }],
            Target = inputs + @"\target",
            OutputDirectory = root + @"\Library\Omnipatching\probe",
        };
        switch (invalidPath)
        {
            case "source": config.Sources[0].Path = @"C:\InstalledGame\media.wem"; break;
            case "target": config.Target = @"C:\InstalledGame\media.wem"; break;
            case "traversal": config.Target = inputs + @"\..\..\..\target"; break;
            case "prefix": config.Target = root + @"\reference\omnipatching-other\target"; break;
            case "junction":
                fileSystem.Directory.CreateSymbolicLink(inputs + @"\link", inputs);
                config.Target = inputs + @"\link\target";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(invalidPath));
        }

        Assert.Throws<ArgumentException>(() => RunProbe(fileSystem, root, config));

        Assert.That(fileSystem.Directory.Exists(config.OutputDirectory), Is.False);
    }

    /// <summary>
    /// Loads configured range hints from JSON and records their payload reuse in persisted evidence.
    /// </summary>
    [Test]
    public void ProbeJsonRangeHintsReuseInlinePayloadAndRecordMetrics()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\ReduxHintProbe";
        byte[] payload = Enumerable.Range(0, 200_000).Select(index => (byte)(index / 32768)).ToArray();
        byte[] prefix = Enumerable.Repeat((byte)0xFE, 71).ToArray();
        byte[] suffix = Enumerable.Repeat((byte)0xFD, 43).ToArray();
        fileSystem.Directory.CreateDirectory(root);
        fileSystem.File.WriteAllBytes(root + @"\resource.bin", payload);
        fileSystem.File.WriteAllBytes(root + @"\target.bundle", SyntheticBundles.Bundle(false,
            ("inline-cab", prefix.Concat(payload).Concat(suffix).ToArray(), true)));
        var config = new ProbeConfig
        {
            Sources = [new ProbeSource { Path = root + @"\resource.bin", Kind = "RawFile" }],
            Target = root + @"\target.bundle",
            OutputDirectory = root + @"\reference\omnipatching\probe",
            RangeHints =
            [
                new BundleRecipeRangeHint
                {
                    TargetEntryIndex = 0,
                    TargetOffset = prefix.Length,
                    SourceIndex = 0,
                    SourceOffset = 0,
                    Length = payload.Length,
                    Sha256 = Hex.Sha256(payload),
                },
            ],
        };
        var restoredConfig = JsonCodec.Read<ProbeConfig>(JsonCodec.Write(config))!;

        RunProbe(fileSystem, root, restoredConfig);

        byte[] canonical = fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\canonical.bundle");
        Assert.That(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\reconstructed.bundle"), Is.EqualTo(canonical));
        Assert.That(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\literals.pack"), Has.Length.EqualTo(prefix.Length + suffix.Length));
        using var evidence = System.Text.Json.JsonDocument.Parse(fileSystem.File.ReadAllBytes(config.OutputDirectory + @"\generation.json"));
        var metrics = evidence.RootElement.GetProperty("Metrics");
        Assert.That(metrics.GetProperty("HintedReusedBytes").GetInt64(), Is.EqualTo(payload.Length));
        Assert.That(metrics.GetProperty("CdcReusedBytes").GetInt64(), Is.Zero);
        Assert.That(metrics.GetProperty("VerifiedRangeHints").GetInt32(), Is.EqualTo(1));
    }

    private static void RunProbe(IFileSystem fileSystem, string root, ProbeConfig config)
    {
        string output = fileSystem.Path.GetFullPath(config.OutputDirectory);
        RequireContainedPath(fileSystem, root, output);
        if (fileSystem.Directory.Exists(output))
            throw new ArgumentException("Use a new output directory so the probe cannot overwrite earlier evidence.");
        bool isRaw = config.TargetKind switch
        {
            "UnityFS" => false,
            "RawFile" => true,
            _ => throw new InvalidDataException($"Unknown target kind '{config.TargetKind}'."),
        };
        if (isRaw)
        {
            RequireContainedPath(fileSystem, root, config.Target);
            foreach (var source in config.Sources)
            {
                RequireContainedPath(fileSystem, root, source.Path);
            }
        }
        var sources = new List<Stream>();
        try
        {
            var descriptors = new List<BundleRecipeSource>();
            foreach (var source in config.Sources)
            {
                if (!Enum.TryParse(source.Kind, ignoreCase: true, out BundleRecipeSourceKind kind))
                    throw new InvalidDataException($"Unknown source kind '{source.Kind}'.");
                Stream input = fileSystem.File.Open(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                sources.Add(input);
                descriptors.Add(BundleRecipe.DescribeSource(input, kind));
            }
            using Stream target = fileSystem.File.Open(config.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
            fileSystem.Directory.CreateDirectory(output);
            using Stream pack = fileSystem.File.Open(fileSystem.Path.Combine(output, "literals.pack"), FileMode.CreateNew, FileAccess.ReadWrite);
            using Stream? canonical = isRaw ? null : fileSystem.File.Open(fileSystem.Path.Combine(output, "canonical.bundle"), FileMode.CreateNew, FileAccess.ReadWrite);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            BundleRecipeGenerationResult result = isRaw
                ? BundleRecipe.GenerateRaw(descriptors, sources, target, pack, config.Options, config.RangeHints)
                : BundleRecipe.Generate(descriptors, sources, target, pack, canonical!, config.Options, config.RangeHints);
            long generationAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            long workingSetAfterGeneration = process.WorkingSet64;
            byte[] recipeBytes = JsonCodec.Write(result.Recipe);
            fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(output, "recipe.json"), recipeBytes);
            var restored = JsonCodec.Read<BundleRecipe>(recipeBytes)!;
            using Stream reconstructed = fileSystem.File.Open(fileSystem.Path.Combine(output, isRaw ? "reconstructed.file" : "reconstructed.bundle"), FileMode.CreateNew, FileAccess.ReadWrite);
            var reconstructionTimer = System.Diagnostics.Stopwatch.StartNew();
            restored.Reconstruct(sources, pack, reconstructed);
            reconstructionTimer.Stop();
            Stream expected = isRaw ? target : canonical!;
            AssertStreamsEqual(expected, reconstructed);
            reconstructed.Position = 0;
            string reconstructedHash = Hex.Sha256(reconstructed);
            pack.Position = 0;
            fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(output, "generation.json"), JsonCodec.Write(new
            {
                config.TargetKind,
                RecipeVersion = result.Recipe.Version,
                result.Recipe.PackingProfile,
                result.Recipe.MatchingProfile,
                SourceDescriptors = descriptors,
                result.NativeTargetSha256,
                result.NativeTargetLength,
                result.Recipe.TargetSha256,
                result.Recipe.TargetLength,
                CanonicalTargetSha256 = isRaw ? null : result.Recipe.TargetSha256,
                CanonicalTargetLength = isRaw ? (long?)null : result.Recipe.TargetLength,
                ReconstructedSha256 = reconstructedHash,
                ReconstructedLength = reconstructed.Length,
                ReconstructionMilliseconds = reconstructionTimer.Elapsed.TotalMilliseconds,
                GenerationThreadAllocatedBytes = generationAllocatedBytes,
                ProcessWorkingSetBytesAfterGeneration = workingSetAfterGeneration,
                ByteComparisonVerified = true,
                EntryCount = result.Recipe.Entries.Count,
                RecipeSha256 = Hex.Sha256(recipeBytes),
                LiteralPackSha256 = Hex.Sha256(pack),
                result.Metrics,
                result.LiteralChunks,
            }));
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }
    }

    private static void RequireContainedPath(IFileSystem fileSystem, string root, string path)
    {
        string fullPath = fileSystem.Path.GetFullPath(path);
        string checkout = fileSystem.Path.GetFullPath(root);
        string reference = fileSystem.Path.Combine(checkout, "reference", "omnipatching") + fileSystem.Path.DirectorySeparatorChar;
        string library = fileSystem.Path.Combine(checkout, "Library", "Omnipatching") + fileSystem.Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(reference, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(library, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Probe paths must be below reference/omnipatching or Library/Omnipatching in this checkout.");
        string? ancestor = fullPath;
        while (ancestor is not null)
        {
            if ((fileSystem.File.Exists(ancestor) || fileSystem.Directory.Exists(ancestor)) &&
                (fileSystem.File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Probe paths must not traverse symlinks or junctions.");
            ancestor = fileSystem.Path.GetDirectoryName(ancestor);
        }
    }

    private static void AssertStreamsEqual(Stream expected, Stream actual)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length));
        expected.Position = 0;
        actual.Position = 0;
        var expectedBytes = new byte[65536];
        var actualBytes = new byte[65536];
        long compared = 0;
        while (compared < expected.Length)
        {
            int count = (int)Math.Min(expectedBytes.Length, expected.Length - compared);
            expected.ReadExactly(expectedBytes.AsSpan(0, count));
            actual.ReadExactly(actualBytes.AsSpan(0, count));
            Assert.That(actualBytes.AsSpan(0, count).SequenceEqual(expectedBytes.AsSpan(0, count)), Is.True,
                $"Independent target comparison differs in range starting at {compared}.");
            compared += count;
        }
    }

    // This shape belongs only to the local probe's caller-controlled JSON configuration.
    private class ProbeConfig
    {
        /// <summary>
        /// Gets or sets copied native source files in deterministic source-index order.
        /// </summary>
        public List<ProbeSource> Sources { get; set; } = new();

        /// <summary>
        /// Gets or sets the copied or newly built target file.
        /// </summary>
        public string Target { get; set; } = "";

        /// <summary>
        /// Gets or sets UnityFS for legacy canonical probes or RawFile for exact native target bytes.
        /// </summary>
        public string TargetKind { get; set; } = "UnityFS";

        /// <summary>
        /// Gets or sets a new output folder inside this checkout's ignored scratch roots.
        /// </summary>
        public string OutputDirectory { get; set; } = "";

        /// <summary>
        /// Gets or sets the explicit generation budgets.
        /// </summary>
        public BundleRecipeGenerationOptions Options { get; set; } = new();

        /// <summary>
        /// Gets or sets measured target-entry and source-coordinate payload reuse claims.
        /// </summary>
        public List<BundleRecipeRangeHint> RangeHints { get; set; } = new();
    }

    private class ProbeSource
    {
        /// <summary>
        /// Gets or sets the copied native file path.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// Gets or sets RawFile or UnityFsData as the range coordinate kind.
        /// </summary>
        public string Kind { get; set; } = "";
    }
}
