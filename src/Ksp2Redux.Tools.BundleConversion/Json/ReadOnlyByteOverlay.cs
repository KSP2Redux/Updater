using Ksp2Redux.Tools.BundleConversion.Bundles;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// The .NET assembly bridge to the converter's canonical read-only byte overlay.
/// </summary>
/// <remarks>
/// This bridge stays outside the Unity package root. Its source lease adapter keeps the caller's
/// actual file handle open when a disposable overlay view is consumed by another assembly.
/// </remarks>
public static class ReadOnlyByteOverlay
{
    /// <summary>
    /// Open a disposable read-only view without taking ownership of the underlying source lease.
    /// </summary>
    /// <param name="source">The caller-owned readable, seekable source.</param>
    /// <param name="replacements">The independently validated offsets and replacement bytes.</param>
    /// <returns>The canonical overlay view, whose disposal leaves the source open.</returns>
    /// <remarks>The caller validates range declarations and authenticates the complete resulting bytes.</remarks>
    public static Stream Open(Stream source, IReadOnlyList<KeyValuePair<long, byte[]>> replacements)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (replacements is null)
            throw new ArgumentNullException(nameof(replacements));
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("A readable, seekable source lease is required.", nameof(source));
        return new OverlayStream(new SourceLeaseStream(source), replacements);
    }

    private sealed class SourceLeaseStream(Stream source) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => source.CanRead;
        /// <inheritdoc />
        public override bool CanSeek => source.CanSeek;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => source.Length;
        /// <inheritdoc />
        public override long Position { get => source.Position; set => source.Position = value; }
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        /// <inheritdoc />
        public override void Flush() { }
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
