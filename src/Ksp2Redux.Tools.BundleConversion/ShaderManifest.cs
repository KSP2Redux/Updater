using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// The replacement shader bundle manifest the Redux shader bundle build writes next to its bundles.
/// </summary>
// Same shape as Redux.Assets.ShaderBundleManifest in the Unity project, which writes it with
// JsonUtility. Only Bundles[].Cab and the shader names and path IDs drive the conversion.
public sealed class ShaderManifest
{
    /// <summary>
    /// Gets or sets the serialization format version.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets the exact Unity version that compiled the shaders.
    /// </summary>
    public string? UnityVersion { get; set; }

    /// <summary>
    /// Gets or sets the target platform of the shader binaries.
    /// </summary>
    public string? Platform { get; set; }

    /// <summary>
    /// Gets or sets the hash of the sources the bundles were built from.
    /// </summary>
    public string? SourceHash { get; set; }

    /// <summary>
    /// Gets or sets the replacement bundles.
    /// </summary>
    public List<ShaderManifestBundle> Bundles { get; set; } = [];
}

/// <summary>
/// One replacement bundle and the stock bundle it stands in for.
/// </summary>
public sealed class ShaderManifestBundle
{
    /// <summary>
    /// Gets or sets the stock bundle file name the Addressables catalog references.
    /// </summary>
    public string? StockFile { get; set; }

    /// <summary>
    /// Gets or sets the replacement bundle file name next to the manifest.
    /// </summary>
    public string? File { get; set; }

    /// <summary>
    /// Gets or sets the internal serialized file name shared with the stock bundle.
    /// </summary>
    public string Cab { get; set; } = "";

    /// <summary>
    /// Gets or sets the SHA-256 of the replacement bundle.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Gets or sets the shaders in the bundle.
    /// </summary>
    public List<ShaderManifestShader> Shaders { get; set; } = [];
}

/// <summary>
/// A shader in a replacement bundle.
/// </summary>
public sealed class ShaderManifestShader
{
    /// <summary>
    /// Gets or sets the shader name, identical to the stock shader it replaces.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the object path ID inside the bundle, as a decimal string.
    /// </summary>
    public string PathId { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether the path ID is the stock one rather than a synthetic one.
    /// </summary>
    public bool Stock { get; set; }
}

/// <summary>
/// A place a shader can be retargeted to: a replacement bundle's CAB and the shader's path ID in it.
/// </summary>
/// <param name="Cab">The internal serialized file name of the replacement bundle.</param>
/// <param name="PathId">The path ID of the shader inside that file.</param>
public sealed record ShaderTarget(string Cab, long PathId);

/// <summary>
/// A loaded shader manifest, indexed by shader name, with the hash of the file it came from.
/// </summary>
public sealed class ShaderManifestIndex
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, List<ShaderTarget>> _targets = new(StringComparer.Ordinal);

    private ShaderManifestIndex(ShaderManifest manifest, string sha256)
    {
        Manifest = manifest;
        ManifestSha256 = sha256;

        HashSet<string> replacementCabs = new(StringComparer.OrdinalIgnoreCase)
        {
            ShaderBundleIds.SHARED_SHADERS_CAB,
            ShaderBundleIds.BUILTIN_SHADERS_CAB,
        };

        List<string> warnings = [];
        foreach (var bundle in manifest.Bundles)
        {
            if (string.IsNullOrWhiteSpace(bundle.Cab))
            {
                warnings.Add($"Manifest bundle '{bundle.File}' has no Cab and was ignored.");
                continue;
            }

            replacementCabs.Add(bundle.Cab);
            foreach (var shader in bundle.Shaders)
            {
                if (!long.TryParse(shader.PathId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pathId))
                {
                    warnings.Add($"Shader '{shader.Name}' in {bundle.Cab} has an unreadable PathId '{shader.PathId}' and was ignored.");
                    continue;
                }

                if (!_targets.TryGetValue(shader.Name, out var list))
                {
                    list = [];
                    _targets.Add(shader.Name, list);
                }

                list.Add(new ShaderTarget(bundle.Cab, pathId));
            }
        }

        ReplacementCabs = replacementCabs;
        Warnings = warnings;
    }

    /// <summary>
    /// Gets the manifest as read from disk.
    /// </summary>
    public ShaderManifest Manifest { get; }

    /// <summary>
    /// Gets the lowercase hex SHA-256 of the manifest file.
    /// </summary>
    public string ManifestSha256 { get; }

    /// <summary>
    /// Gets the CABs a material may already point into without needing a retarget: the two stock
    /// shared shader bundles and every bundle the manifest lists.
    /// </summary>
    public IReadOnlyCollection<string> ReplacementCabs { get; }

    /// <summary>
    /// Gets problems found while indexing the manifest. None of them stop a conversion.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Gets the number of distinct shader names the manifest can retarget to.
    /// </summary>
    public int ShaderCount => _targets.Count;

    /// <summary>
    /// Reads and indexes a manifest file.
    /// </summary>
    /// <param name="fileSystem">The file system to read through.</param>
    /// <param name="path">The path to manifest.json.</param>
    /// <returns>The indexed manifest.</returns>
    /// <exception cref="InvalidDataException">The file is not a readable manifest.</exception>
    public static ShaderManifestIndex Load(IFileSystem fileSystem, string path)
    {
        var bytes = fileSystem.File.ReadAllBytes(path);
        return FromBytes(bytes);
    }

    /// <summary>
    /// Indexes a manifest from the bytes of its file.
    /// </summary>
    /// <param name="bytes">The manifest.json contents.</param>
    /// <returns>The indexed manifest.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a readable manifest.</exception>
    public static ShaderManifestIndex FromBytes(byte[] bytes)
    {
        ShaderManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ShaderManifest>(StripBom(bytes), JSON_OPTIONS);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The shader manifest is not valid JSON: {e.Message}", e);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("The shader manifest is empty.");
        }

        return new ShaderManifestIndex(manifest, Hex.Sha256(bytes));
    }

    /// <summary>
    /// Says whether a CAB belongs to a replacement shader bundle.
    /// </summary>
    /// <param name="cab">The internal serialized file name.</param>
    /// <returns>True when materials pointing into it need nothing.</returns>
    public bool IsReplacementCab(string cab) => ReplacementCabs.Contains(cab);

    /// <summary>
    /// Finds where a shader can be retargeted to.
    /// </summary>
    /// <param name="shaderName">The shader name.</param>
    /// <param name="target">Every replacement location of the shader, in manifest order.</param>
    /// <returns>True when the manifest has the shader.</returns>
    public bool TryFind(string shaderName, out IReadOnlyList<ShaderTarget> target)
    {
        if (_targets.TryGetValue(shaderName, out var list))
        {
            target = list;
            return true;
        }

        target = [];
        return false;
    }

    // JsonUtility and some editors write a byte order mark, which the UTF-8 reader rejects.
    private static ReadOnlySpan<byte> StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsSpan(3) : bytes;
}
