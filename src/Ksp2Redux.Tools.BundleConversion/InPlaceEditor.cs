namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Reads, classifies and writes same-size edits in an open bundle file.
/// </summary>
internal static class InPlaceEditor
{
    /// <summary>
    /// Reads bytes at an offset.
    /// </summary>
    /// <param name="stream">The file.</param>
    /// <param name="offset">Where to read.</param>
    /// <param name="count">How many bytes to read.</param>
    /// <returns>The bytes, fewer than asked for when the file ends first.</returns>
    public static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || offset >= stream.Length)
        {
            return [];
        }

        stream.Position = offset;
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var chunk = stream.Read(buffer, read, count - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return read == count ? buffer : buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>
    /// Says what a file holds where an edit goes.
    /// </summary>
    /// <param name="stream">The file.</param>
    /// <param name="offset">The edit offset.</param>
    /// <param name="oldBytes">The stock bytes.</param>
    /// <param name="newBytes">The converted bytes.</param>
    /// <returns>Applied, Stock or Foreign.</returns>
    public static EditState Classify(Stream stream, long offset, byte[] oldBytes, byte[] newBytes)
    {
        var current = ReadAt(stream, offset, newBytes.Length);
        if (current.AsSpan().SequenceEqual(newBytes))
        {
            return EditState.Applied;
        }

        return current.AsSpan().SequenceEqual(oldBytes) ? EditState.Stock : EditState.Foreign;
    }

    /// <summary>
    /// Writes bytes at an offset, but only when the file currently holds the expected bytes there.
    /// </summary>
    /// <param name="stream">The file, open for writing.</param>
    /// <param name="offset">The edit offset.</param>
    /// <param name="expected">The bytes the file must hold before the write.</param>
    /// <param name="replacement">The bytes to write.</param>
    /// <returns>True when the bytes were written.</returns>
    // The guard is what makes an edit safe to run against a file that changed since it was planned:
    // a mismatch leaves the file alone rather than writing a PPtr into the middle of something else.
    public static bool WriteIfMatches(Stream stream, long offset, byte[] expected, byte[] replacement)
    {
        if (expected.Length != replacement.Length)
        {
            throw new ArgumentException("An in-place edit cannot change the file size.", nameof(replacement));
        }

        var current = ReadAt(stream, offset, expected.Length);
        if (!current.AsSpan().SequenceEqual(expected))
        {
            return false;
        }

        stream.Position = offset;
        stream.Write(replacement, 0, replacement.Length);
        return true;
    }
}
