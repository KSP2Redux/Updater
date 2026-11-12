using Ksp2Redux.Tools.BundleConversion.Textures;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

public class Bc7EncoderTest
{
    private static readonly int[] WEIGHTS = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    [Test]
    public void EncodeBlock_SmoothGradient_DecodesWithinHalfAPaletteStep()
    {
        // Arrange: a diagonal ramp in every channel, the kind of block the smooth cloud maps are made of.
        var pixels = new float[64];
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var t = ((pixel & 3) + (pixel >> 2)) / 6f;
            pixels[(pixel * 4) + 0] = 40f + (150f * t);
            pixels[(pixel * 4) + 1] = 200f - (120f * t);
            pixels[(pixel * 4) + 2] = 90f + (30f * t);
            pixels[(pixel * 4) + 3] = 255f;
        }

        // Act
        var block = new byte[Bc7Encoder.BLOCK_BYTES];
        Bc7Encoder.EncodeBlock(pixels, block);

        // Assert: the widest channel spans 150 levels, and 16 palette entries 4 or 5 sixty-fourths apart put no pixel
        // further than about 6 levels from one.
        Assert.That(MaxError(DecodeMode6(block), pixels), Is.LessThanOrEqualTo(6f));
    }

    [Test]
    public void EncodeBlock_FirstPixelAtTheBrightEnd_StillDecodesItsValue()
    {
        // Arrange: BC7 stores the first pixel's index without its top bit, so a first pixel at the bright end only
        // decodes right when the encoder swaps the endpoints.
        var pixels = new float[64];
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var value = pixel == 0 ? 250f : 10f + (pixel * 3f);
            for (var channel = 0; channel < 4; channel++)
            {
                pixels[(pixel * 4) + channel] = value;
            }
        }

        // Act
        var block = new byte[Bc7Encoder.BLOCK_BYTES];
        Bc7Encoder.EncodeBlock(pixels, block);

        // Assert
        var decoded = DecodeMode6(block);
        Assert.That(decoded[0], Is.EqualTo(250f).Within(3f));
        Assert.That(MaxError(decoded, pixels), Is.LessThanOrEqualTo(8f));
    }

    [Test]
    public void Encode_ImageSmallerThanABlock_FillsOneBlockFromItsEdgePixels()
    {
        // Arrange: the 2x2 tail of a mip chain, four greys.
        float[] pixels = [0f, 0f, 0f, 255f, 80f, 80f, 80f, 255f, 160f, 160f, 160f, 255f, 240f, 240f, 240f, 255f];

        // Act
        var output = new byte[Bc7Encoder.EncodedSize(2, 2)];
        Bc7Encoder.Encode(pixels, 2, 2, output, 0);

        // Assert: the four real pixels sit at the block's top left.
        Assert.That(output, Has.Length.EqualTo(Bc7Encoder.BLOCK_BYTES));
        var decoded = DecodeMode6(output);
        int[] corners = [0, 1, 4, 5];
        for (var index = 0; index < corners.Length; index++)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                Assert.That(decoded[(corners[index] * 4) + channel], Is.EqualTo(pixels[(index * 4) + channel]).Within(4f));
            }
        }
    }

    /// <summary>
    /// Decodes a BC7 mode 6 block to 16 RGBA pixels.
    /// </summary>
    internal static float[] DecodeMode6(byte[] block, int offset = 0)
    {
        var low = BitConverter.ToUInt64(block, offset);
        var high = BitConverter.ToUInt64(block, offset + 8);
        var position = 0;
        int Read(int bits)
        {
            ulong value = position < 64 ? low >> position : high >> (position - 64);
            if (position < 64 && position + bits > 64)
            {
                value |= high << (64 - position);
            }

            position += bits;
            return (int)(value & ((1UL << bits) - 1));
        }

        Assert.That(Read(7), Is.EqualTo(1 << 6), "not a mode 6 block");
        var endpoints = new int[2, 4];
        for (var channel = 0; channel < 4; channel++)
        {
            endpoints[0, channel] = Read(7);
            endpoints[1, channel] = Read(7);
        }

        var lowBit = Read(1);
        var highBit = Read(1);
        var pixels = new float[64];
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var weight = WEIGHTS[Read(pixel == 0 ? 3 : 4)];
            for (var channel = 0; channel < 4; channel++)
            {
                var from = (endpoints[0, channel] << 1) | lowBit;
                var to = (endpoints[1, channel] << 1) | highBit;
                pixels[(pixel * 4) + channel] = (((64 - weight) * from) + (weight * to) + 32) >> 6;
            }
        }

        return pixels;
    }

    private static float MaxError(float[] decoded, float[] source) =>
        decoded.Zip(source, (a, b) => Math.Abs(a - b)).Max();
}
