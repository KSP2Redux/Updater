namespace Ksp2Redux.Tools.BundleConversion.Textures;

/// <summary>
/// Encodes images to BC7 with mode 6 only: one RGBA endpoint pair per 4x4 block, 7 bits per channel and a p-bit per
/// endpoint, and a 4-bit index per pixel.
/// </summary>
/// <remarks>
/// One mode keeps the encoder small, and it suits the smooth cloud maps it is used for. Each block starts from its
/// principal axis, and for each of the four p-bit pairs the endpoints are refitted by least squares to the indices
/// they produce. The pair that decodes closest to the source wins.
/// </remarks>
internal static class Bc7Encoder
{
    /// <summary>
    /// The bytes of one encoded 4x4 block.
    /// </summary>
    public const int BLOCK_BYTES = 16;

    private const int BLOCK_PIXELS = 16;
    private const int CHANNELS = 4;
    private const int BLOCK_VALUES = BLOCK_PIXELS * CHANNELS;
    private const int REFINE_PASSES = 2;
    private const int POWER_ITERATIONS = 8;
    private const int MODE_6 = 1 << 6;
    private const int MODE_BITS = 7;
    private const int ENDPOINT_BITS = 7;
    private const int MAX_ENDPOINT = (1 << ENDPOINT_BITS) - 1;
    private const int INDEX_BITS = 4;
    private const int ANCHOR_INDEX_BITS = INDEX_BITS - 1;
    private const int INDEX_COUNT = 1 << INDEX_BITS;
    private const int WEIGHT_TOTAL = 64;
    private const float MAX_VALUE = 255f;

    // The BC7 interpolation weights for 4-bit indices, out of 64.
    private static readonly int[] WEIGHTS = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>
    /// Gets the encoded size of an image.
    /// </summary>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The size in bytes. Images narrower or shorter than a block still take whole blocks.</returns>
    public static int EncodedSize(int width, int height) => BlockCount(width) * BlockCount(height) * BLOCK_BYTES;

    /// <summary>
    /// Encodes one image, block rows in parallel.
    /// </summary>
    /// <param name="pixels">The pixels, row by row, four channels from 0 to 255 each.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="output">Receives the blocks, row by row.</param>
    /// <param name="outputOffset">Where the first block goes in <paramref name="output" />.</param>
    /// <param name="cancellationToken">Stops the encode between block rows.</param>
    // A block that hangs over the image edge repeats the edge pixels, so the padding never pulls the fit away from
    // the pixels a sampler can reach.
    public static void Encode(float[] pixels, int width, int height, byte[] output, int outputOffset, CancellationToken cancellationToken = default)
    {
        var across = BlockCount(width);
        var rows = BlockCount(height);
        ParallelOptions options = new() { CancellationToken = cancellationToken };
        Parallel.For(0, rows, options, row =>
        {
            Span<float> block = stackalloc float[BLOCK_VALUES];
            for (var column = 0; column < across; column++)
            {
                for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
                {
                    var x = Math.Min((column * 4) + (pixel & 3), width - 1);
                    var y = Math.Min((row * 4) + (pixel >> 2), height - 1);
                    var source = ((y * width) + x) * CHANNELS;
                    for (var channel = 0; channel < CHANNELS; channel++)
                    {
                        block[(pixel * CHANNELS) + channel] = pixels[source + channel];
                    }
                }

                EncodeBlock(block, output.AsSpan(outputOffset + (((row * across) + column) * BLOCK_BYTES), BLOCK_BYTES));
            }
        });
    }

    /// <summary>
    /// Encodes one 4x4 block.
    /// </summary>
    /// <param name="pixels">The 16 pixels, row by row, four channels from 0 to 255 each.</param>
    /// <param name="block">Receives the 16 encoded bytes.</param>
    public static void EncodeBlock(ReadOnlySpan<float> pixels, Span<byte> block)
    {
        Span<float> low = stackalloc float[CHANNELS];
        Span<float> high = stackalloc float[CHANNELS];
        AxisEndpoints(pixels, low, high);

        Span<int> bestLow = stackalloc int[CHANNELS];
        Span<int> bestHigh = stackalloc int[CHANNELS];
        Span<byte> bestIndices = stackalloc byte[BLOCK_PIXELS];
        int bestLowBit = 0, bestHighBit = 0;
        var bestError = float.MaxValue;

        Span<int> quantLow = stackalloc int[CHANNELS];
        Span<int> quantHigh = stackalloc int[CHANNELS];
        Span<byte> indices = stackalloc byte[BLOCK_PIXELS];
        Span<float> fitLow = stackalloc float[CHANNELS];
        Span<float> fitHigh = stackalloc float[CHANNELS];

        for (var pBits = 0; pBits < 4; pBits++)
        {
            var lowBit = pBits & 1;
            var highBit = pBits >> 1;
            low.CopyTo(fitLow);
            high.CopyTo(fitHigh);

            for (var pass = 0; pass <= REFINE_PASSES; pass++)
            {
                Quantize(fitLow, lowBit, quantLow);
                Quantize(fitHigh, highBit, quantHigh);
                var error = FitIndices(pixels, quantLow, lowBit, quantHigh, highBit, indices);
                if (error < bestError)
                {
                    bestError = error;
                    quantLow.CopyTo(bestLow);
                    quantHigh.CopyTo(bestHigh);
                    indices.CopyTo(bestIndices);
                    bestLowBit = lowBit;
                    bestHighBit = highBit;
                }

                if (error == 0f || pass == REFINE_PASSES || !LeastSquares(pixels, indices, fitLow, fitHigh))
                {
                    break;
                }
            }
        }

        Pack(bestLow, bestLowBit, bestHigh, bestHighBit, bestIndices, block);
    }

    private static int BlockCount(int pixels) => Math.Max(1, (pixels + 3) / 4);

    // Endpoints at the ends of the block's projection on its principal axis, through the mean.
    private static void AxisEndpoints(ReadOnlySpan<float> pixels, Span<float> low, Span<float> high)
    {
        Span<float> mean = stackalloc float[CHANNELS];
        for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
        {
            for (var channel = 0; channel < CHANNELS; channel++)
            {
                mean[channel] += pixels[(pixel * CHANNELS) + channel];
            }
        }

        for (var channel = 0; channel < CHANNELS; channel++)
        {
            mean[channel] /= BLOCK_PIXELS;
        }

        Span<float> covariance = stackalloc float[CHANNELS * CHANNELS];
        for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
        {
            for (var row = 0; row < CHANNELS; row++)
            {
                var deviation = pixels[(pixel * CHANNELS) + row] - mean[row];
                for (var column = 0; column < CHANNELS; column++)
                {
                    covariance[(row * CHANNELS) + column] += deviation * (pixels[(pixel * CHANNELS) + column] - mean[column]);
                }
            }
        }

        // Power iteration from the column of the channel that varies most.
        var widest = 0;
        for (var channel = 1; channel < CHANNELS; channel++)
        {
            if (covariance[(channel * CHANNELS) + channel] > covariance[(widest * CHANNELS) + widest])
            {
                widest = channel;
            }
        }

        Span<float> axis = stackalloc float[CHANNELS];
        Span<float> next = stackalloc float[CHANNELS];
        for (var channel = 0; channel < CHANNELS; channel++)
        {
            axis[channel] = covariance[(channel * CHANNELS) + widest];
        }

        var length = Normalize(axis);
        for (var iteration = 0; iteration < POWER_ITERATIONS && length > 0f; iteration++)
        {
            for (var row = 0; row < CHANNELS; row++)
            {
                var sum = 0f;
                for (var column = 0; column < CHANNELS; column++)
                {
                    sum += covariance[(row * CHANNELS) + column] * axis[column];
                }

                next[row] = sum;
            }

            next.CopyTo(axis);
            length = Normalize(axis);
        }

        if (length <= 0f)
        {
            // A flat block: both endpoints sit on the mean, and the p-bits and indices do the rest.
            mean.CopyTo(low);
            mean.CopyTo(high);
            return;
        }

        float minimum = float.MaxValue, maximum = float.MinValue;
        for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
        {
            var projection = 0f;
            for (var channel = 0; channel < CHANNELS; channel++)
            {
                projection += (pixels[(pixel * CHANNELS) + channel] - mean[channel]) * axis[channel];
            }

            minimum = Math.Min(minimum, projection);
            maximum = Math.Max(maximum, projection);
        }

        for (var channel = 0; channel < CHANNELS; channel++)
        {
            low[channel] = Clamp(mean[channel] + (minimum * axis[channel]));
            high[channel] = Clamp(mean[channel] + (maximum * axis[channel]));
        }
    }

    private static float Normalize(Span<float> vector)
    {
        var squared = 0f;
        foreach (var value in vector)
        {
            squared += value * value;
        }

        if (squared < 1e-12f)
        {
            return 0f;
        }

        var length = MathF.Sqrt(squared);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] /= length;
        }

        return length;
    }

    private static void Quantize(ReadOnlySpan<float> endpoint, int pBit, Span<int> quantized)
    {
        for (var channel = 0; channel < CHANNELS; channel++)
        {
            var value = (int)MathF.Round((endpoint[channel] - pBit) * 0.5f);
            quantized[channel] = Math.Max(0, Math.Min(MAX_ENDPOINT, value));
        }
    }

    // Picks each pixel's closest palette entry and returns the summed squared error.
    private static float FitIndices(
        ReadOnlySpan<float> pixels,
        ReadOnlySpan<int> low,
        int lowBit,
        ReadOnlySpan<int> high,
        int highBit,
        Span<byte> indices)
    {
        Span<float> palette = stackalloc float[INDEX_COUNT * CHANNELS];
        for (var channel = 0; channel < CHANNELS; channel++)
        {
            var from = (low[channel] << 1) | lowBit;
            var to = (high[channel] << 1) | highBit;
            for (var index = 0; index < INDEX_COUNT; index++)
            {
                var weight = WEIGHTS[index];
                palette[(index * CHANNELS) + channel] = (((WEIGHT_TOTAL - weight) * from) + (weight * to) + 32) >> 6;
            }
        }

        var total = 0f;
        for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
        {
            var best = float.MaxValue;
            var bestIndex = 0;
            for (var index = 0; index < INDEX_COUNT; index++)
            {
                var error = 0f;
                for (var channel = 0; channel < CHANNELS; channel++)
                {
                    var difference = palette[(index * CHANNELS) + channel] - pixels[(pixel * CHANNELS) + channel];
                    error += difference * difference;
                }

                if (error < best)
                {
                    best = error;
                    bestIndex = index;
                }
            }

            indices[pixel] = (byte)bestIndex;
            total += best;
        }

        return total;
    }

    // The endpoints that best reproduce the pixels with these indices, per channel. False when every pixel uses
    // the same weight, which leaves the pair undetermined.
    private static bool LeastSquares(ReadOnlySpan<float> pixels, ReadOnlySpan<byte> indices, Span<float> low, Span<float> high)
    {
        float lowLow = 0f, lowHigh = 0f, highHigh = 0f;
        Span<float> lowSum = stackalloc float[CHANNELS];
        Span<float> highSum = stackalloc float[CHANNELS];
        for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
        {
            var weight = WEIGHTS[indices[pixel]] / (float)WEIGHT_TOTAL;
            var lowWeight = 1f - weight;
            lowLow += lowWeight * lowWeight;
            lowHigh += lowWeight * weight;
            highHigh += weight * weight;
            for (var channel = 0; channel < CHANNELS; channel++)
            {
                var value = pixels[(pixel * CHANNELS) + channel];
                lowSum[channel] += lowWeight * value;
                highSum[channel] += weight * value;
            }
        }

        var determinant = (lowLow * highHigh) - (lowHigh * lowHigh);
        if (Math.Abs(determinant) < 1e-6f)
        {
            return false;
        }

        for (var channel = 0; channel < CHANNELS; channel++)
        {
            low[channel] = Clamp(((highHigh * lowSum[channel]) - (lowHigh * highSum[channel])) / determinant);
            high[channel] = Clamp(((lowLow * highSum[channel]) - (lowHigh * lowSum[channel])) / determinant);
        }

        return true;
    }

    // Writes the block, swapping the endpoints when the first pixel's index has its top bit set, because the anchor
    // index is stored without it.
    private static void Pack(ReadOnlySpan<int> low, int lowBit, ReadOnlySpan<int> high, int highBit, ReadOnlySpan<byte> indices, Span<byte> block)
    {
        var swap = indices[0] >= INDEX_COUNT / 2;
        var first = swap ? high : low;
        var second = swap ? low : high;
        BitWriter writer = new();
        writer.Write(MODE_6, MODE_BITS);
        for (var channel = 0; channel < CHANNELS; channel++)
        {
            writer.Write(first[channel], ENDPOINT_BITS);
            writer.Write(second[channel], ENDPOINT_BITS);
        }

        writer.Write(swap ? highBit : lowBit, 1);
        writer.Write(swap ? lowBit : highBit, 1);
        for (var pixel = 0; pixel < BLOCK_PIXELS; pixel++)
        {
            var index = swap ? INDEX_COUNT - 1 - indices[pixel] : indices[pixel];
            writer.Write(index, pixel == 0 ? ANCHOR_INDEX_BITS : INDEX_BITS);
        }

        writer.CopyTo(block);
    }

    private static float Clamp(float value) => Math.Max(0f, Math.Min(MAX_VALUE, value));

    /// <summary>
    /// Fills a 128-bit block from its lowest bit up.
    /// </summary>
    private ref struct BitWriter
    {
        private ulong _low;
        private ulong _high;
        private int _position;

        public void Write(int value, int bits)
        {
            var bitsValue = (ulong)value & ((1UL << bits) - 1);
            if (_position < 64)
            {
                _low |= bitsValue << _position;
                if (_position + bits > 64)
                {
                    _high |= bitsValue >> (64 - _position);
                }
            }
            else
            {
                _high |= bitsValue << (_position - 64);
            }

            _position += bits;
        }

        public readonly void CopyTo(Span<byte> block)
        {
            for (var index = 0; index < 8; index++)
            {
                block[index] = (byte)(_low >> (index * 8));
                block[index + 8] = (byte)(_high >> (index * 8));
            }
        }
    }
}
