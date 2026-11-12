using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Reads and writes the journal and manifest JSON with System.Text.Json.
/// </summary>
/// <remarks>
/// It sits outside Source because the Unity editor, which compiles Source as a package, has no
/// System.Text.Json. Source/Unity/JsonCodec.cs is the same surface on Newtonsoft.Json there.
/// </remarks>
internal static class JsonCodec
{
    private static readonly JsonSerializerOptions READ_OPTIONS = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // The relaxed encoder writes shader names such as "<CAB-x:123>" as they are instead of as <
    // escapes. The journal is a data file, never embedded in HTML, so the stricter default buys nothing.
    private static readonly JsonSerializerOptions WRITE_OPTIONS = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
            return JsonSerializer.Deserialize<T>(utf8, READ_OPTIONS);
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
    public static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, WRITE_OPTIONS);
}