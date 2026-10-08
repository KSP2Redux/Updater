using System.Text.Json;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// A KSP2 folder on a mock file system, with helpers to lay out synthetic bundles in it.
/// </summary>
internal sealed class TestGame
{
    public const string ROOT = @"C:\Games\KSP2";

    public const string EMBEDDED_SHADER = "Test/Embedded";
    public const string CONTENT_CAB = "CAB-0000000000000000000000000000c0de";
    public const string OTHER_CAB = "CAB-00000000000000000000000000000ther";
    public const long SHADER_PATH_ID = 5;
    public const long MATERIAL_PATH_ID = 10;
    public const long BEHAVIOUR_PATH_ID = 20;
    public const long SCRIPT_PATH_ID = 30;

    public TestGame()
    {
        Layout = new BundleConversionLayout(FileSystem, ROOT);
        FileSystem.Directory.CreateDirectory(Layout.BundleDirectory);
    }

    public MockFileSystem FileSystem { get; } = new(options => options.SimulatingOperatingSystem(SimulationMode.Windows));

    public BundleConversionLayout Layout { get; }

    public static long TargetPathId => ShaderBundleIds.SyntheticPathId(EMBEDDED_SHADER);

    public BundleConverter Converter() => new(FileSystem, ROOT, new BundleConversionOptions { MaxParallelism = 1 });

    public void Write(string file, byte[] bytes) => FileSystem.File.WriteAllBytes(Layout.BundlePath(file), bytes);

    public byte[] Read(string file) => FileSystem.File.ReadAllBytes(Layout.BundlePath(file));

    public byte[] ReadCopy(string file) => FileSystem.File.ReadAllBytes(Layout.Resolve(BundleConversionLayout.RelativeOutput(file)));

    public bool CopyExists(string file) => FileSystem.File.Exists(Layout.Resolve(BundleConversionLayout.RelativeOutput(file)));

    public ConversionJournal? Journal() => new JournalStore(FileSystem, Layout.JournalPath).Read();

    /// <summary>
    /// A manifest that puts every given shader name in the stock shared shader bundle at its synthetic path ID.
    /// </summary>
    public static ShaderManifestIndex Manifest(params string[] shaderNames) =>
        Manifest(shaderNames.Select(name => (name, ShaderBundleIds.SHARED_SHADERS_CAB)).ToArray());

    public static ShaderManifestIndex Manifest(params (string Name, string Cab)[] shaders)
    {
        ShaderManifest manifest = new() { Version = 1, UnityVersion = "6000.6.0f1", Platform = "StandaloneWindows64" };
        foreach (var group in shaders.GroupBy(shader => shader.Cab))
        {
            manifest.Bundles.Add(new ShaderManifestBundle
            {
                Cab = group.Key,
                Shaders = [.. group.Select(shader => new ShaderManifestShader { Name = shader.Name, PathId = ShaderBundleIds.SyntheticPathId(shader.Name).ToString() })],
            });
        }

        return ShaderManifestIndex.FromBytes(JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    /// <summary>
    /// A content bundle with an embedded shader copy, a material using it, a MonoBehaviour with a
    /// shader field pointing at it, and an AssetBundle object whose preload table lists both.
    /// </summary>
    public static byte[] ContentBundle(bool compress, params string[] externals)
    {
        var file = new SerializedFileBuilder(CONTENT_CAB);
        foreach (var external in externals)
        {
            file.External(external);
        }

        file.Script(0, SCRIPT_PATH_ID)
            .Object(1, SyntheticBundles.ASSET_BUNDLE, SyntheticBundles.AssetBundle("content", (0, MATERIAL_PATH_ID), (0, SHADER_PATH_ID), (0, BEHAVIOUR_PATH_ID)))
            .Object(SHADER_PATH_ID, SyntheticBundles.SHADER, SyntheticBundles.Shader(EMBEDDED_SHADER))
            .Object(MATERIAL_PATH_ID, SyntheticBundles.MATERIAL, SyntheticBundles.Material("TestMaterial", 0, SHADER_PATH_ID))
            .Object(BEHAVIOUR_PATH_ID, SyntheticBundles.MONO_BEHAVIOUR, SyntheticBundles.MonoBehaviour(SCRIPT_PATH_ID, 0, SHADER_PATH_ID))
            .Object(SCRIPT_PATH_ID, SyntheticBundles.MONO_SCRIPT, SyntheticBundles.MonoScript("Test", "BlurThing"));

        return SyntheticBundles.Bundle(
            compress,
            (CONTENT_CAB, file.Build(), true),
            (CONTENT_CAB + ".resS", Enumerable.Range(0, 300_000).Select(index => (byte)(index * 7)).ToArray(), false));
    }

    /// <summary>
    /// A bundle whose only material uses the shader embedded in <see cref="ContentBundle" />.
    /// </summary>
    public static byte[] ReferencingBundle(bool compress, params string[] externals)
    {
        var file = new SerializedFileBuilder(OTHER_CAB).External(CONTENT_CAB);
        foreach (var external in externals)
        {
            file.External(external);
        }

        file.Object(1, SyntheticBundles.ASSET_BUNDLE, SyntheticBundles.AssetBundle("other", (0, 2), (1, SHADER_PATH_ID)))
            .Object(2, SyntheticBundles.MATERIAL, SyntheticBundles.Material("OtherMaterial", 1, SHADER_PATH_ID));

        return SyntheticBundles.Bundle(compress, (OTHER_CAB, file.Build(), true));
    }
}
