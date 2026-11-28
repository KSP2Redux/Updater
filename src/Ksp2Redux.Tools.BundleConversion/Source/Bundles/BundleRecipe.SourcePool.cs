using AssetsTools.NET;

namespace Ksp2Redux.Tools.BundleConversion.Bundles;

public partial class BundleRecipe
{
    /// <summary>
    /// A bounded pool of decoded readers shared across release outputs.
    /// </summary>
    /// <remarks>
    /// Native streams remain owned by the caller, who must exclude concurrent source writers.
    /// </remarks>
    internal sealed class SourcePool : IDisposable
    {
        private readonly IReadOnlyList<Stream> _native;
        private readonly IReadOnlyList<BundleRecipeSource> _descriptors;
        private readonly Dictionary<int, UnityFsBundle> _readers = new();
        private readonly LinkedList<int> _recent = new();
        /// <summary>
        /// Gets the seekable coordinate views in native descriptor order.
        /// </summary>
        internal Stream[] Data { get; }
        /// <summary>
        /// Gets source directory boundaries for deterministic chunking.
        /// </summary>
        internal List<AssetBundleDirectoryInfo>?[] Directories { get; }

        /// <summary>
        /// Validate coordinate metadata and create lazy source views.
        /// </summary>
        /// <param name="native">Stable authenticated native streams.</param>
        /// <param name="descriptors">The matching coordinate descriptors.</param>
        internal SourcePool(IReadOnlyList<Stream> native, IReadOnlyList<BundleRecipeSource> descriptors)
        {
            _native = native;
            _descriptors = descriptors;
            Data = new Stream[native.Count];
            Directories = new List<AssetBundleDirectoryInfo>?[native.Count];
            long directoryBytes = 0;
            for (int index = 0; index < native.Count; index++)
            {
                if (descriptors[index].Kind == BundleRecipeSourceKind.UnityFsData)
                {
                    using var bundle = OpenSource(native[index]);
                    ValidateSource(bundle, descriptors[index]);
                    directoryBytes = checked(directoryBytes + bundle.Directory.Sum(entry => 128L + entry.Name.Length * 2L));
                    if (directoryBytes > 64L * 1024 * 1024)
                        throw new InvalidDataException("Release source directory metadata exceeds 64 MiB.");
                    Directories[index] = bundle.Directory;
                }
                Data[index] = new SourceView(this, index, descriptors[index].DataLength);
            }
        }

        private Stream Get(int index)
        {
            if (_descriptors[index].Kind == BundleRecipeSourceKind.RawFile)
                return _native[index];
            if (_readers.TryGetValue(index, out var existing))
            {
                _recent.Remove(index);
                _recent.AddLast(index);
                return existing.Data;
            }
            if (_readers.Count == 4)
            {
                int victim = _recent.First!.Value;
                _recent.RemoveFirst();
                _readers[victim].Dispose();
                _readers.Remove(victim);
            }
            using var metadataTime = RecipePerformance.Measure("source-reader-open-metadata");
            var bundle = OpenSource(_native[index]);
            ValidateSource(bundle, _descriptors[index]);
            _readers.Add(index, bundle);
            _recent.AddLast(index);
            return bundle.Data;
        }

        internal static void ValidateCoordinates(Stream data, BundleRecipeSource descriptor)
        {
            if (data is not SourceView view)
                throw new InvalidDataException("Decoded source has no release owner.");
            view.ValidateDescriptor(descriptor);
        }

        public void Dispose()
        {
            foreach (var reader in _readers.Values)
                reader.Dispose();
            _readers.Clear();
            _recent.Clear();
        }

        private sealed class SourceView : Stream
        {
            private readonly SourcePool _owner;
            private readonly int _index;
            private readonly long _length;
            internal SourceView(SourcePool owner, int index, long length)
            {
                _owner = owner;
                _index = index;
                _length = length;
            }
            internal void ValidateDescriptor(BundleRecipeSource descriptor)
            {
                if (descriptor.Kind != _owner._descriptors[_index].Kind || descriptor.DataLength != Length)
                    throw new InvalidDataException("Source coordinate descriptor mismatch.");
                if (descriptor.Kind == BundleRecipeSourceKind.UnityFsData)
                {
                    _owner.Get(_index);
                    ValidateSource(_owner._readers[_index], descriptor);
                }
            }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get; set; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Stream input = _owner.Get(_index);
                input.Position = Position;
                int read = input.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, Length - Position)));
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
