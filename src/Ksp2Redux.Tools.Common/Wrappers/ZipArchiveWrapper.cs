using System.IO.Abstractions;
using System.IO.Compression;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Common.Wrappers;

public interface IZipArchive : IDisposable
{
    /// <summary>
    /// Gets all central-directory entries, including duplicate names.
    /// </summary>
    IReadOnlyList<IZipArchiveEntry> Entries { get; }
    ZipArchiveMode Mode { get; }
    IZipArchiveEntry? GetEntry(string entryName);
    IZipArchiveEntry CreateEntry(string entryName);
    /// <summary>
    /// Create an entry with an explicit compression profile.
    /// </summary>
    /// <param name="entryName">The archive-relative name.</param>
    /// <param name="compressionLevel">The compression level.</param>
    /// <returns>The new entry.</returns>
    IZipArchiveEntry CreateEntry(string entryName, CompressionLevel compressionLevel);
    void ExtractToDirectory(IFileSystem fileSystem, string destinationDirectoryName, bool overwriteFiles);
    IZipArchiveEntry CreateEntryFromFile(string sourceFileName, string entryName);
}

#pragma warning disable RS0030
/// <summary>
/// Wrapper of <see cref="ZipArchive"/> to go through the interface <see cref="IZipArchive"/>.
/// Prefer using <see cref="IZipArchive"/> when possible.
/// </summary>
public class ZipArchiveWrapper(ZipArchive archive) : IZipArchive
{
    /// <inheritdoc />
    public IReadOnlyList<IZipArchiveEntry> Entries => archive.Entries
        .Select(entry => (IZipArchiveEntry)new ZipArchiveEntryWrapper(entry)).ToArray();

    public ZipArchiveMode Mode => archive.Mode;

    public IZipArchiveEntry? GetEntry(string entryName)
    {
        ZipArchiveEntry? zipArchiveEntry = archive.GetEntry(entryName);
        
        return zipArchiveEntry is null
            ? null
            : new ZipArchiveEntryWrapper(zipArchiveEntry);
    }

    public IZipArchiveEntry CreateEntry(string entryName)
    => new ZipArchiveEntryWrapper(archive.CreateEntry(entryName));

    /// <inheritdoc />
    public IZipArchiveEntry CreateEntry(string entryName, CompressionLevel compressionLevel)
        => new ZipArchiveEntryWrapper(archive.CreateEntry(entryName, compressionLevel));

    public void ExtractToDirectory(IFileSystem fileSystem, string destinationDirectoryName, bool overwriteFiles)
    {
        // FileSystem is an argument only for this check, to prevent unwanted interaction with the real file system
        if (fileSystem is not RealFileSystem)
            throw new InvalidOperationException("Extracting a real zip archive in a mock file system is not supported. Please use a mock zip archive instead.");
        archive.ExtractToDirectory(destinationDirectoryName, overwriteFiles);
    }
    
    public IZipArchiveEntry CreateEntryFromFile(string sourceFileName, string entryName)
        => new ZipArchiveEntryWrapper(archive.CreateEntryFromFile(sourceFileName, entryName));

    public void Dispose()
    {
        archive.Dispose();
    }
}

public interface IZipArchiveEntry
{
    /// <summary>
    /// Gets the complete entry name without filesystem normalization.
    /// </summary>
    string FullName { get; }
    /// <summary>
    /// Gets the declared uncompressed byte length.
    /// </summary>
    long Length { get; }
    /// <summary>
    /// Gets the platform attributes used to detect links and special files.
    /// </summary>
    int ExternalAttributes { get; set; }
    Stream Open();
    void ExtractToFile(IFileSystem fileSystem, string destinationFileName);
}

/// <summary>
/// Wrapper of <see cref="ZipArchiveEntry"/> to go through the interface <see cref="IZipArchiveEntry"/>.
/// Prefer using <see cref="IZipArchiveEntry"/> when possible.
/// </summary>
/// <param name="entry">The original entry.</param>
public class ZipArchiveEntryWrapper(ZipArchiveEntry entry) : IZipArchiveEntry
{
    /// <inheritdoc />
    public string FullName => entry.FullName;
    /// <inheritdoc />
    public long Length => entry.Length;
    /// <inheritdoc />
    public int ExternalAttributes { get => entry.ExternalAttributes; set => entry.ExternalAttributes = value; }

    public Stream Open()
    => entry.Open();
    
    public void ExtractToFile(IFileSystem fileSystem, string destinationFileName)
    {
        // FileSystem is an argument only for this check, to prevent unwanted interaction with the real file system
        if (fileSystem is not RealFileSystem)
            throw new InvalidOperationException("Extracting a real zip archive in a mock file system is not supported. Please use a mock zip archive instead.");
        entry.ExtractToFile(destinationFileName);
    }
}
#pragma warning restore RS0030
