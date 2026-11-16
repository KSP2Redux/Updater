namespace Ksp2Redux.Tools.Common.ReleasePreparation;

public partial class ReleaseRecipePipeline
{
    private sealed class PackReader(ReleaseRecipePipeline owner, ReleaseRecipeManifest manifest, Func<int, string> path) : IDisposable
    {
        private readonly Dictionary<int, Stream> _streams = new();
        private readonly LinkedList<int> _recent = new();

        public byte[] Read(int chunkIndex)
        {
            using var literalTime = BundleConversion.Bundles.RecipePerformance.Measure("literal-read-decode-hash-inclusive");
            var chunk = manifest.Chunks[chunkIndex];
            if (_streams.TryGetValue(chunk.PackIndex, out Stream? input))
            {
                _recent.Remove(chunk.PackIndex);
            }
            else
            {
                if (_streams.Count == 8)
                {
                    int victim = _recent.First!.Value;
                    _recent.RemoveFirst();
                    _streams[victim].Dispose();
                    _streams.Remove(victim);
                }
                input = owner.OpenRead(path(chunk.PackIndex));
                _streams.Add(chunk.PackIndex, input);
                BundleConversion.Bundles.RecipePerformance.ObservePackHandles(_streams.Count);
            }
            _recent.AddLast(chunk.PackIndex);
            if (input.Length != manifest.Packs[chunk.PackIndex].Length)
                throw new InvalidDataException("Release pack length changed during preparation.");
            return ReadChunk(input, chunk);
        }

        public void Dispose()
        {
            foreach (Stream input in _streams.Values)
                input.Dispose();
            _streams.Clear();
            _recent.Clear();
        }
    }

    // A virtual literal stream retains only one decoded chunk. Recipes can seek across pack boundaries.
    private sealed class ReleasePayloadStream : Stream
    {
        private readonly PackReader _reader;
        private readonly List<int> _chunks;
        private readonly long[] _starts;
        private readonly CancellationToken _ct;
        private byte[] _cache = [];
        private int _cachedIndex = -1;

        /// <summary>
        /// Construct the seek index over verified chunk records.
        /// </summary>
        /// <param name="reader">The release pack reader shared across target files.</param>
        /// <param name="manifest">The validated release metadata.</param>
        /// <param name="chunks">The ordered virtual payload chunks.</param>
        /// <param name="ct">The cancellation token.</param>
        public ReleasePayloadStream(PackReader reader, ReleaseRecipeManifest manifest,
            List<int> chunks, CancellationToken ct)
        {
            _reader = reader;
            _chunks = chunks;
            _ct = ct;
            _starts = new long[chunks.Count + 1];
            for (int index = 0; index < chunks.Count; index++)
            {
                _starts[index + 1] = checked(_starts[index] + manifest.Chunks[chunks[index]].Length);
            }
        }

        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => true;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => _starts[_starts.Length - 1];
        /// <inheritdoc />
        public override long Position { get; set; }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (Position < 0 || Position > Length)
                throw new IOException("Virtual literal stream position is outside its bounds.");
            int written = 0;
            while (written < count && Position < Length)
            {
                _ct.ThrowIfCancellationRequested();
                int index = Array.BinarySearch(_starts, Position);
                if (index < 0)
                    index = ~index - 1;
                if (_cachedIndex != index)
                {
                    _cache = _reader.Read(_chunks[index]);
                    _cachedIndex = index;
                }
                int start = checked((int)(Position - _starts[index]));
                int length = Math.Min(count - written, _cache.Length - start);
                Buffer.BlockCopy(_cache, start, buffer, offset + written, length);
                written += length;
                Position += length;
            }
            return written;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0 || next > Length)
                throw new IOException("Virtual literal seek exceeds its bounds.");
            return Position = next;
        }

        /// <inheritdoc />
        public override void Flush() { }
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
