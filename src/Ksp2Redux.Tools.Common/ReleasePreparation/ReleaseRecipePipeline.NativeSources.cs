namespace Ksp2Redux.Tools.Common.ReleasePreparation;

public partial class ReleaseRecipePipeline
{
    // Stable trees are a caller precondition. Verify their full digests before and after use,
    // then verify every output independently. Reopening never grants authority to new bytes.
    private sealed class NativeSourcePool : IDisposable
    {
        private readonly ReleaseRecipePipeline _owner;
        private readonly Dictionary<string, string[]> _listings;
        private readonly Dictionary<string, Stream> _open = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _recent = new();
        internal NativeSourcePool(ReleaseRecipePipeline owner, Dictionary<string, string[]>? listings = null)
        {
            _owner = owner;
            _listings = listings ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        }

        internal Stream Create(string path, long length)
        {
            return new NativeView(this, path, length);
        }

        private Stream Get(string path, long length)
        {
            if (_open.TryGetValue(path, out var existing))
            {
                _recent.Remove(path);
                _recent.AddLast(path);
                return existing;
            }
            if (_open.Count == 16)
            {
                string victim = _recent.First!.Value;
                _recent.RemoveFirst();
                _open[victim].Dispose();
                _open.Remove(victim);
            }
            Stream input = _owner.OpenRead(path, _listings);
            if (input.Length != length)
            {
                input.Dispose();
                throw new InvalidDataException("A matching source changed length.");
            }
            _open.Add(path, input);
            BundleConversion.Bundles.RecipePerformance.ObserveSourceHandles(_open.Count);
            _recent.AddLast(path);
            return input;
        }

        public void Dispose()
        {
            foreach (Stream input in _open.Values)
                input.Dispose();
            _open.Clear();
        }

        private sealed class NativeView : Stream
        {
            private readonly NativeSourcePool _pool;
            private readonly string _path;
            private readonly long _length;
            internal NativeView(NativeSourcePool pool, string path, long length)
            {
                _pool = pool;
                _path = path;
                _length = length;
            }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get; set; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                using var ioTime = BundleConversion.Bundles.RecipePerformance.Measure("matching-source-native-read-io");
                Stream input = _pool.Get(_path, Length);
                input.Position = Position;
                int read = input.Read(buffer, offset, count);
                Position += read;
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
