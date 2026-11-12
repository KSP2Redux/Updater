using System.IO.Abstractions;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Converts, verifies and reverts copies of real KSP2 bundles. Runs only when
/// KSP2REDUX_BUNDLE_TEST_GAME names a folder laid out like a KSP2 install, holding copied bundles
/// under KSP2_x64_Data/StreamingAssets/aa/StandaloneWindows64. That folder is never written to: the
/// test works on a copy of it in the temp folder.
/// </summary>
[Category("Integration")]
public class RealBundlesIntegrationTest
{
    private const string GAME_VARIABLE = "KSP2REDUX_BUNDLE_TEST_GAME";
    private const string OPAQUE = "KSP2/Scenery/Standard (Opaque)";
    private const long OPAQUE_STOCK_PATH_ID = -4264630710741740062;

    private static readonly string[] PORTED =
    [
        "Reentry/Shockwave",
        "KSP2/Scenery/Standard (Procedural)",
        "KSP2/Environment/CelestialBody/CelestialBody_Local_Shadow",
        "Hidden/Amplify Impostors/Octahedron Impostor",
        "KSP2/Scenery/Standard Tileable Distance Blend",
    ];

    [Test]
    public void ConvertTwiceVerifyAndRevert_CopiedBundles_EndByteIdenticalToStock()
    {
        // Arrange
        var source = System.Environment.GetEnvironmentVariable(GAME_VARIABLE);
        if (string.IsNullOrWhiteSpace(source))
        {
            Assert.Ignore($"Set {GAME_VARIABLE} to a folder of copied KSP2 bundles to run this test.");
        }

        IFileSystem fileSystem = new RealFileSystem();
        var sourceLayout = new BundleConversionLayout(fileSystem, source!);
        var game = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "ksp2redux-bundle-test-" + Guid.NewGuid().ToString("N"));
        var layout = new BundleConversionLayout(fileSystem, game);
        fileSystem.Directory.CreateDirectory(layout.BundleDirectory);
        Dictionary<string, string> stockHashes = [];
        foreach (var path in fileSystem.Directory.EnumerateFiles(sourceLayout.BundleDirectory, "*.bundle"))
        {
            var name = fileSystem.Path.GetFileName(path);
            fileSystem.File.Copy(path, layout.BundlePath(name));
            using var stream = fileSystem.File.OpenRead(path);
            stockHashes[name] = Hex.Sha256(stream);
        }

        ShaderManifest manifest = new()
        {
            Version = 1,
            Bundles =
            [
                new ShaderManifestBundle
                {
                    Cab = ShaderBundleIds.SHARED_SHADERS_CAB,
                    Shaders =
                    [
                        new ShaderManifestShader { Name = OPAQUE, PathId = OPAQUE_STOCK_PATH_ID.ToString(), Stock = true },
                        .. PORTED.Select(name => new ShaderManifestShader { Name = name, PathId = ShaderBundleIds.SyntheticPathId(name).ToString() }),
                    ],
                },
            ],
        };

        var index = ShaderManifestIndex.FromBytes(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest));
        var converter = new BundleConverter(fileSystem, game, new BundleConversionOptions { MaxParallelism = 4 });

        try
        {
            // Act
            var first = converter.Apply(index);
            var second = converter.Apply(index);
            var verify = converter.Verify();
            var revert = converter.Revert();

            // Assert
            Assert.That(first.Plan.Errors, Is.Empty);
            Assert.That(first.Failures, Is.Empty);
            Assert.That(first.ForeignEdits, Is.Empty);
            Assert.That(first.Plan.Bundles, Is.Not.Empty, "the copied bundles hold no shader the test manifest ports");
            Assert.That(second.EditsWritten + second.EditsUpdated + second.CopiesWritten, Is.Zero);
            Assert.That(verify.IsComplete, Is.True);
            Assert.That(revert.JournalDeleted, Is.True);
            Assert.That(fileSystem.Directory.Exists(layout.ConversionDirectory), Is.False);
            foreach (var (name, hash) in stockHashes)
            {
                using var stream = fileSystem.File.OpenRead(layout.BundlePath(name));
                Assert.That(Hex.Sha256(stream), Is.EqualTo(hash), name);
            }
        }
        finally
        {
            fileSystem.Directory.Delete(game, recursive: true);
        }
    }
}
