using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    [Test, Explicit, NonParallelizable]
    public void MeasurePublishedGenerationUpdate()
    {
        string settings = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_UPDATE_MEASUREMENT")
            ?? throw new InvalidOperationException("Supply isolated measurement settings.");
        using var config = JsonDocument.Parse(File.ReadAllBytes(settings));
        var c = config.RootElement;
        string work = c.GetProperty("work").GetString()!;
        var fs = new RealFileSystem();
        var pipeline = new ReleaseRecipePipeline(fs);
        StockBaselineManifest Normalize(string key, string manifestKey)
        {
            string source = c.GetProperty(key).GetString()!;
            var manifest = StockBaselineManifest.FromJson(File.ReadAllBytes(c.GetProperty(manifestKey).GetString()!));
            string destination = Path.Combine(work, key);
            if (Directory.Exists(destination))
            {
                AssertTreeStreaming(fs, destination, manifest);
                return manifest;
            }
            Directory.CreateDirectory(destination);
            foreach (string directory in manifest.Directories)
                Directory.CreateDirectory(Path.Combine(destination, directory));
            foreach (var file in manifest.Files)
            {
                string outputPath = Path.Combine(destination, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var input = File.OpenRead(Path.Combine(source, file.Path));
                using var output = File.Open(outputPath, FileMode.CreateNew);
                byte[] magic = new byte[8];
                if (input.Length >= 8) input.ReadExactly(magic);
                input.Position = 0;
                if (magic.AsSpan().SequenceEqual("UnityFS\0"u8)) BundleRecipe.Canonicalize(input, output);
                else input.CopyTo(output);
            }
            AssertTreeStreaming(fs, destination, manifest);
            return manifest;
        }
        var prior = Normalize("previous", "previousManifest");
        var target = Normalize("next", "nextManifest");
        var selector = System.Reflection.Assembly.Load("Ksp2Redux.Tools.Uploader")
            .GetType("Ksp2Redux.Tools.Uploader.FullSourcePublication")!
            .GetMethod("SelectUpdateSources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var sources = (IReadOnlyList<ReleaseRecipeSource>)selector.Invoke(null,
            [new Ksp2Redux.Tools.Common.Transactions.InstallTransactionEngine(fs), Path.Combine(work, "previous"), prior])!;
        var canonical = target.Files.Where(file =>
        {
            using var input = File.OpenRead(Path.Combine(work, "next", file.Path));
            byte[] magic = new byte[8];
            if (input.Length >= 8) input.ReadExactly(magic);
            return magic.AsSpan().SequenceEqual("UnityFS\0"u8);
        }).Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var results = new List<object>();
        foreach (bool reuse in new[] { false, true })
        {
            string artifact = Path.Combine(work, reuse ? "after" : "before");
            var timer = Stopwatch.StartNew();
            var publication = pipeline.PublishHosted(Path.Combine(work, "next"), target,
                [new ReleaseRecipeSourceTree(Path.Combine(work, "previous"), prior, reuse ? sources : [])], artifact, canonicalPaths: canonical);
            double publishSeconds = timer.Elapsed.TotalSeconds;
            double? applySeconds = null;
            if (reuse)
            {
                timer.Restart();
                pipeline.Prepare(artifact, publication.ArtifactSha256, publication.TargetManifestSha256,
                    Path.Combine(work, "previous"), prior, Path.Combine(work, "applied"));
                applySeconds = timer.Elapsed.TotalSeconds;
                AssertTreeStreaming(fs, Path.Combine(work, "applied"), target);
            }
            results.Add(new { reuse, publication.StoredPackBytes, MarkerBytes = new FileInfo(Path.Combine(artifact, "release.json")).Length,
                publication.SourceReusedBytes, publishSeconds, applySeconds });
            File.WriteAllBytes(Path.Combine(work, "measurement.json"), JsonSerializer.SerializeToUtf8Bytes(results));
        }
    }
}
