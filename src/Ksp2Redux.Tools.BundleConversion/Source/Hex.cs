using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Hex encoding, hashing and PPtr byte helpers.
/// </summary>
// netstandard2.1 has no Convert.ToHexString or SHA256.HashData, so these stand in for them.
internal static class Hex
{
    /// <summary>
    /// The size of a serialized PPtr: an int32 file ID followed by an int64 path ID.
    /// </summary>
    public const int PPTR_SIZE = 12;

    private const string DIGITS = "0123456789abcdef";

    /// <summary>
    /// Encodes bytes as lowercase hex.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>Two lowercase hex digits per byte.</returns>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            chars[index * 2] = DIGITS[bytes[index] >> 4];
            chars[(index * 2) + 1] = DIGITS[bytes[index] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>
    /// Decodes a hex string, upper or lower case.
    /// </summary>
    /// <param name="hex">The hex string.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="FormatException">The string has an odd length or a character that is not a hex digit.</exception>
    public static byte[] Decode(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new FormatException($"Hex string '{hex}' has an odd length.");
        }

        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = byte.Parse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>
    /// Serializes a PPtr the way Unity stores it in little-endian serialized files.
    /// </summary>
    /// <param name="fileId">The file ID: 0 for the same file, otherwise the 1-based external index.</param>
    /// <param name="pathId">The object path ID.</param>
    /// <returns>The 12 PPtr bytes.</returns>
    public static byte[] PPtr(int fileId, long pathId)
    {
        var bytes = new byte[PPTR_SIZE];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, fileId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), pathId);
        return bytes;
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of a stream from its current position to the end.
    /// </summary>
    /// <param name="stream">The stream to hash.</param>
    /// <returns>The hash as lowercase hex.</returns>
    public static string Sha256(Stream stream)
    {
        using var sha256 = SHA256.Create();
        return Encode(sha256.ComputeHash(stream));
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of a byte array.
    /// </summary>
    /// <param name="bytes">The bytes to hash.</param>
    /// <returns>The hash as lowercase hex.</returns>
    public static string Sha256(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return Encode(sha256.ComputeHash(bytes));
    }
}
