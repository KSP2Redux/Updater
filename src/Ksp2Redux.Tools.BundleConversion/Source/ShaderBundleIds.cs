using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Identifiers shared by the Redux shader bundle build, the game runtime and this converter.
/// </summary>
// Mirrors Redux.Assets.ShaderBundleIds in the Unity project. The two must agree, or retargeted
// materials point at path IDs the replacement bundle does not contain.
public static class ShaderBundleIds
{
    /// <summary>
    /// The internal serialized file name of the stock sharedshaders_assets_all.bundle.
    /// </summary>
    public const string SHARED_SHADERS_CAB = "CAB-4ed2d59f8739dcd96dd03eb1eb005bf2";

    /// <summary>
    /// The internal serialized file name of the stock Unity built-in shader bundle.
    /// </summary>
    public const string BUILTIN_SHADERS_CAB = "CAB-4e84b80d43e9e6cd9dbf681a89f3c468";

    /// <summary>
    /// The prefix hashed with a shader name to derive its synthetic path ID.
    /// </summary>
    public const string SYNTHETIC_PREFIX = "redux-urp:";

    /// <summary>
    /// Computes the path ID a ported shader gets when the stock shared bundle has no slot for it.
    /// </summary>
    /// <param name="shaderName">The shader name.</param>
    /// <returns>The first eight bytes of SHA-256 of the prefixed name, read as a little-endian signed integer.</returns>
    public static long SyntheticPathId(string shaderName)
    {
        using var sha256 = SHA256.Create();
        var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(SYNTHETIC_PREFIX + shaderName));
        return BinaryPrimitives.ReadInt64LittleEndian(digest);
    }
}
