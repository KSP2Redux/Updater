using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.ReleasePreparation;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourcePreparationServiceTest
{
    [Test, Explicit, NonParallelizable]
    public void MeasureInstalledGenerationSparseUpdate()
    {
        string settings = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_UPDATE_MEASUREMENT")
            ?? throw new InvalidOperationException("Supply isolated measurement settings.");
        using var config = JsonDocument.Parse(File.ReadAllBytes(settings));
        var c = config.RootElement;
        string work = c.GetProperty("work").GetString()!;
        var fs = new RealFileSystem();
        byte[] priorBytes = File.ReadAllBytes(c.GetProperty("previousManifest").GetString()!);
        byte[] targetBytes = File.ReadAllBytes(c.GetProperty("nextManifest").GetString()!);
        var prior = StockBaselineManifest.FromJson(priorBytes);
        var target = StockBaselineManifest.FromJson(targetBytes);
        string previous = Path.Combine(work, "previous");
        string artifact = Path.Combine(work, "after");
        string marker = Path.Combine(artifact, "release.json");
        string markerSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(marker)));
        string targetSha = Convert.ToHexString(SHA256.HashData(targetBytes));
        var pipeline = new ReleaseRecipePipeline(fs);
        var recipe = pipeline.Inspect(artifact, markerSha, targetSha);
        string?[] packs = recipe.Packs.Select((_, i) => (string?)Path.Combine(artifact, $"pack-{i:D6}.bin")).ToArray();
        var timer = Stopwatch.StartNew();
        AssertTreeStreaming(fs, previous, prior);
        double sourceVerifySeconds = timer.Elapsed.TotalSeconds;
        string stage = Path.Combine(work, "sparse-applied");
        timer.Restart();
        var prepared = pipeline.PrepareDownloadedUpdate(marker, markerSha, targetSha, packs, previous, prior, stage);
        double preparationSeconds = timer.Elapsed.TotalSeconds;
        var staged = ReleaseRecipePipeline.UpdateStageContract(target, prior);
        timer.Restart();
        Assert.That(prepared.ToJson(), Is.EqualTo(targetBytes));
        AssertTreeStreaming(fs, stage, staged);
        var stagedPaths = staged.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var file in target.Files)
        {
            string path = Path.Combine(stagedPaths.Contains(file.Path) ? stage : previous, file.Path);
            Assert.That(new FileInfo(path).Length, Is.EqualTo(file.Length), file.Path);
            Assert.That(HashFile(fs, path), Is.EqualTo(file.Sha256), file.Path);
        }
        double verifySeconds = timer.Elapsed.TotalSeconds;
        File.WriteAllBytes(Path.Combine(work, "sparse-measurement.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceVerifySeconds, preparationSeconds, verifySeconds,
            stagedFiles = staged.Files.Count, stagedBytes = staged.TotalBytes,
            verifiedFiles = target.Files.Count, verifiedBytes = target.TotalBytes,
            sourceManifestSha256 = Convert.ToHexString(SHA256.HashData(priorBytes)), targetManifestSha256 = targetSha,
            downloadBytes = new FileInfo(marker).Length + packs.Sum(p => new FileInfo(p!).Length),
            completeGenerationHashesVerified = true
        }));
    }
}
