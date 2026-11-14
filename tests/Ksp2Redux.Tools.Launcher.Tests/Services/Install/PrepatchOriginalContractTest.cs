using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class PrepatchOriginalContractTest
{
    [Test]
    public void EmbeddedSteamOriginalHashes_DeriveTheCompleteOriginalContractWithoutClaimingPrepatchedBytesAsStock()
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        var resources = new Dictionary<string, string>();
        PatchManifest? package = null;
        foreach (string name in new[] { "Steam", "Epic", "Portable" })
        {
            using Stream source = typeof(InstallPlanService).Assembly.GetManifestResourceStream(
                $"Ksp2Redux.Tools.Launcher.Prepatches.{name.ToLowerInvariant()}-prepatch.patch")!;
            resources[name] = Convert.ToHexString(SHA256.HashData(source));
            if (name != "Steam") continue;
            source.Position = 0;
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, true);
            using Stream metadata = zip.GetEntry("manifest.json")!.Open();
            package = JsonSerializer.Deserialize<PatchManifest>(metadata)!;
        }
        var originals = new Dictionary<string, StockBaselineFile>(StringComparer.Ordinal);
        var normalized = new Dictionary<string, StockBaselineFile>(StringComparer.Ordinal);
        var ignored = FileInformation.IgnoreFiles(fs);
        foreach (PatchOperation operation in package!.Operations)
        {
            string path = operation.FileName.Replace('\\', '/');
            if (operation.Action == PatchOperation.PatchAction.Patch)
            {
                string original = Convert.ToHexString(operation.OriginalHash!);
                string final = ignored.Contains(path.Replace('/', fs.Path.DirectorySeparatorChar))
                    ? original : Convert.ToHexString(operation.FinalHash!);
                originals[path] = new StockBaselineFile(path, 10, original);
                normalized[path] = new StockBaselineFile(path, 20, final);
                if (ignored.Contains(path.Replace('/', fs.Path.DirectorySeparatorChar)))
                    normalized[path] = originals[path];
            }
            else if (operation.Action == PatchOperation.PatchAction.Add)
                normalized[path] = new StockBaselineFile(path, 1, Convert.ToHexString(operation.FinalHash!));
            else if (path.EndsWith("Steamworks.NET.txt", StringComparison.Ordinal))
                originals[path] = new StockBaselineFile(path, 1, new string('1', 64));
        }
        const string streamed = "KSP2_x64_Data/StreamingAssets/fixture.bin";
        originals[streamed] = normalized[streamed] = new StockBaselineFile(streamed, 1, new string('2', 64));
        var stock = new StockBaselineManifest("fixture-original", "metadata-only original fixture", originals.Values);
        var prepatch = new StockBaselineManifest("fixture-prepatch", "metadata-only normalization fixture", normalized.Values);
        byte[] bytes = prepatch.ToJson();
        var descriptor = new FullSourceReleaseDescriptor("fixture", "internal", "0.2.9.0.1", bytes,
            Convert.ToHexString(SHA256.HashData(bytes)), "https://fixture.test/release.json", "https://fixture.test/release.json",
            [new FullSourceBaselineDefinition(stock.ToJson(), [])], false, 1)
        {
            PrepatchManifest = bytes, PrepatchManifestSha256 = Convert.ToHexString(SHA256.HashData(bytes)), PrepatchResources = resources
        };
        var config = new Mock<ILauncherConfigService>();
        config.SetupGet(value => value.Config).Returns(new LauncherConfig());
        var os = new Mock<IOperatingSystemService>();
        os.Setup(value => value.IsWindows()).Returns(true);
        var service = new FullSourcePreparationService(fs, config.Object, Mock.Of<IManifestReleasesFeedProviderService>(),
            Mock.Of<IPatchDownloadService>(), () => Mock.Of<ISteamDepotDownloader>(), new ZipFileService(fs),
            Mock.Of<IDiskSpaceService>(), os.Object, _ => { });
        var method = typeof(FullSourcePreparationService).GetMethod("DeriveOriginalBaseline", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var definition = (FullSourceBaselineDefinition)method.Invoke(service,
            [descriptor, prepatch, "Steam", @"C:\Absent", stock.Files.ToDictionary(file => file.Path, file => file.Length)])!;
        var derived = StockBaselineManifest.FromJson(definition.Manifest);
        Assert.That(derived.Files, Is.EqualTo(stock.Files));
        Assert.That(derived.Files.Any(file => file.Path == "prepatched.nodelete"), Is.False);
        Assert.That(derived.Files.Single(file => file.Path == "KSP2_x64.exe").Sha256,
            Is.EqualTo(originals["KSP2_x64.exe"].Sha256));
    }
}