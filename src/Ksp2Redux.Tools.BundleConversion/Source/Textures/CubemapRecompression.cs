namespace Ksp2Redux.Tools.BundleConversion.Textures;

/// <summary>
/// Chooses smaller formats for the stock cloud cubemaps and converts their pixels.
/// </summary>
/// <remarks>
/// <para>
/// The scaled and volumetric cloud shaders decode the normal maps' xyz as v * 2 - 1, and every stock cloud
/// configuration sets the normal factor to 0. 8-bit RGBA keeps those maps to within rounding at half the size of
/// their 16-bit floats. BC7 would not do: the maps carry per-pixel noise that a 4x4 block cannot hold, and it came
/// out at 6 of 255 RMS against 0.3 for rounding. The 8-bit cloud maps go to BC7, which holds them at 56 dB.
/// </para>
/// <para>
/// The density maps stay as they are. Their blue channel holds a thousand times the alpha, which no 8-bit or block
/// format stores without a shader that knows to scale it back.
/// </para>
/// </remarks>
internal static class CubemapRecompression
{
    /// <summary>
    /// The revision of the conversion. Raising it makes every earlier conversion run again.
    /// </summary>
    public const int REVISION = ConversionJournal.CURRENT_TEXTURE_REVISION;

    /// <summary>Unity's TextureFormat.RGBA32.</summary>
    public const int RGBA32 = 4;

    /// <summary>Unity's TextureFormat.RGBAHalf.</summary>
    public const int RGBA_HALF = 17;

    /// <summary>Unity's TextureFormat.BC7.</summary>
    public const int BC7 = 25;

    /// <summary>The faces of a cubemap.</summary>
    public const int FACES = 6;

    private const string CLOUD_MAP = "scaledclouds";
    private const string NORMAL_MAP_SUFFIX = "_normal";
    private const int HALF_PIXEL_BYTES = 8;
    private const int RGBA32_PIXEL_BYTES = 4;

    // How far past 0 and 1 a normal map value may sit and still count as rounding in the 16-bit floats.
    private const float RANGE_TOLERANCE = 1f / 1024f;

    private static readonly float[] HALF_TO_FLOAT = BuildHalfTable();

    /// <summary>
    /// Gets whether a texture is one of the stock cloud cubemaps the conversion looks at.
    /// </summary>
    /// <param name="name">The texture's name.</param>
    /// <returns>True for the scaled cloud maps.</returns>
    public static bool IsCloudMap(string name) => name.IndexOf(CLOUD_MAP, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Picks the format a cloud cubemap is stored in after the conversion.
    /// </summary>
    /// <param name="name">The texture's name.</param>
    /// <param name="format">Its stock format.</param>
    /// <param name="target">The new format.</param>
    /// <returns>True when the texture gets a smaller format.</returns>
    public static bool TryChooseFormat(string name, int format, out int target)
    {
        target = format switch
        {
            RGBA_HALF when name.EndsWith(NORMAL_MAP_SUFFIX, StringComparison.OrdinalIgnoreCase) => RGBA32,
            RGBA32 => BC7,
            _ => format,
        };

        return target != format;
    }

    /// <summary>
    /// Gets the size of one face with its mip chain.
    /// </summary>
    /// <param name="format">The texture format.</param>
    /// <param name="width">The top mip's width.</param>
    /// <param name="height">The top mip's height.</param>
    /// <param name="mipCount">The number of mips.</param>
    /// <returns>The size in bytes, or -1 for a format the conversion does not handle.</returns>
    public static long FaceSize(int format, int width, int height, int mipCount)
    {
        long total = 0;
        for (var mip = 0; mip < mipCount; mip++)
        {
            var size = MipSize(format, MipExtent(width, mip), MipExtent(height, mip));
            if (size < 0)
            {
                return -1;
            }

            total += size;
        }

        return total;
    }

    /// <summary>
    /// Converts every face and mip of a cubemap and writes them in Unity's order, face by face with each face's mips
    /// from the largest down.
    /// </summary>
    /// <param name="source">The stock bundle.</param>
    /// <param name="sourceOffset">The file offset of the stock pixels.</param>
    /// <param name="sourceFormat">The stock format.</param>
    /// <param name="targetFormat">The new format.</param>
    /// <param name="width">The top mip's width.</param>
    /// <param name="height">The top mip's height.</param>
    /// <param name="mipCount">The number of mips.</param>
    /// <param name="output">Receives the converted pixels.</param>
    /// <param name="cancellationToken">Stops the conversion between mips.</param>
    /// <exception cref="InvalidDataException">The stock pixels end early, or a normal map holds values outside 0 to 1.</exception>
    public static void Convert(
        Stream source,
        long sourceOffset,
        int sourceFormat,
        int targetFormat,
        int width,
        int height,
        int mipCount,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        var position = sourceOffset;
        for (var face = 0; face < FACES; face++)
        {
            for (var mip = 0; mip < mipCount; mip++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mipWidth = MipExtent(width, mip);
                var mipHeight = MipExtent(height, mip);
                var stock = ReadAt(source, position, (int)MipSize(sourceFormat, mipWidth, mipHeight));
                position += stock.Length;

                var converted = (sourceFormat, targetFormat) switch
                {
                    (RGBA_HALF, RGBA32) => HalfToRgba32(stock),
                    (RGBA32, BC7) => Rgba32ToBc7(stock, mipWidth, mipHeight, cancellationToken),
                    _ => throw new InvalidDataException($"No conversion from texture format {sourceFormat} to {targetFormat}."),
                };

                output.Write(converted, 0, converted.Length);
            }
        }
    }

    private static int MipExtent(int extent, int mip) => Math.Max(1, extent >> mip);

    private static long MipSize(int format, int width, int height) => format switch
    {
        RGBA32 => (long)width * height * RGBA32_PIXEL_BYTES,
        RGBA_HALF => (long)width * height * HALF_PIXEL_BYTES,
        BC7 => Bc7Encoder.EncodedSize(width, height),
        _ => -1,
    };

    private static byte[] HalfToRgba32(byte[] stock)
    {
        var converted = new byte[stock.Length / 2];
        for (var index = 0; index < converted.Length; index++)
        {
            var value = HALF_TO_FLOAT[stock[index * 2] | (stock[(index * 2) + 1] << 8)];
            if (!(value >= -RANGE_TOLERANCE && value <= 1f + RANGE_TOLERANCE))
            {
                throw new InvalidDataException($"A normal map value of {value} lies outside 0 to 1, which 8 bits cannot hold.");
            }

            converted[index] = (byte)((Math.Max(0f, Math.Min(1f, value)) * 255f) + 0.5f);
        }

        return converted;
    }

    private static byte[] Rgba32ToBc7(byte[] stock, int width, int height, CancellationToken cancellationToken)
    {
        var pixels = new float[stock.Length];
        for (var index = 0; index < stock.Length; index++)
        {
            pixels[index] = stock[index];
        }

        var converted = new byte[Bc7Encoder.EncodedSize(width, height)];
        Bc7Encoder.Encode(pixels, width, height, converted, 0, cancellationToken);
        return converted;
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = stream.Read(buffer, read, count - read);
            if (chunk == 0)
            {
                throw new InvalidDataException($"The texture's pixels end {count - read} bytes early at {offset + read}.");
            }

            read += chunk;
        }

        return buffer;
    }

    // Every 16-bit float as a 32-bit one. netstandard2.1 has no Half type.
    private static float[] BuildHalfTable()
    {
        var table = new float[1 << 16];
        for (var bits = 0; bits < table.Length; bits++)
        {
            var sign = (bits >> 15) & 1;
            var exponent = (bits >> 10) & 0x1F;
            var mantissa = bits & 0x3FF;
            float value;
            if (exponent == 0)
            {
                value = mantissa / 1024f / 16384f;
            }
            else if (exponent == 0x1F)
            {
                value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            }
            else
            {
                value = BitConverter.Int32BitsToSingle(((exponent - 15 + 127) << 23) | (mantissa << 13));
            }

            table[bits] = sign == 0 ? value : -value;
        }

        return table;
    }
}
