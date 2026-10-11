using System.Text;
using Newtonsoft.Json;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Reads and writes the journal and manifest JSON with Newtonsoft.Json, for the Unity editor.
/// </summary>
/// <remarks>
/// Unity compiles Source as a local package and has no System.Text.Json, so this replaces
/// Json/JsonCodec.cs there. The csproj leaves this folder out of the .NET build. Newtonsoft matches
/// property names case-insensitively, skips comments and allows trailing commas when reading, and
/// escapes only what JSON requires when writing, as the System.Text.Json version is set up to.
/// </remarks>
internal static class JsonCodec
{
    private static readonly JsonSerializerSettings SETTINGS = new()
    {
        Formatting = Formatting.Indented,
    };

    /// <summary>
    /// Reads a value, matching property names case-insensitively and allowing comments and trailing commas.
    /// </summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="utf8">The UTF-8 JSON, without a byte order mark.</param>
    /// <returns>The value, or null for a JSON null.</returns>
    /// <exception cref="FormatException">The bytes are not valid JSON for the type.</exception>
    public static T? Read<T>(ReadOnlySpan<byte> utf8)
        where T : class
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(utf8.ToArray()), SETTINGS);
        }
        catch (JsonException e)
        {
            throw new FormatException(e.Message, e);
        }
    }

    /// <summary>
    /// Writes a value as indented UTF-8 JSON.
    /// </summary>
    /// <typeparam name="T">The type to write.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The UTF-8 bytes.</returns>
    public static byte[] Write<T>(T value) => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, SETTINGS));
}