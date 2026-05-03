using System.IO.Abstractions;
using System.IO.Compression;

namespace Ksp2Redux.Tools.Common;

public interface IZipArchive : IDisposable
{
    ZipArchiveMode Mode { get; }
    IZipArchiveEntry? GetEntry(string entryName);
    IZipArchiveEntry CreateEntry(string entryName);
    void ExtractToDirectory(IFileSystem fileSystem, string destinationDirectoryName, bool overwriteFiles);
}

#pragma warning disable RS0030
/// <summary>
/// Wrapper of <see cref="ZipArchive"/> to go through the interface <see cref="IZipArchive"/>.
/// Prefer using <see cref="IZipArchive"/> when possible.
/// </summary>
public class ZipArchiveWrapper(ZipArchive archive) : IZipArchive
{
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

    public void ExtractToDirectory(IFileSystem fileSystem, string destinationDirectoryName, bool overwriteFiles)
    {
        // FileSystem is an argument only for this check, to prevent unwanted interaction with the real file system
        if (fileSystem is not FileSystem)
            throw new InvalidOperationException("Extracting a real zip archive in a mock file system is not supported. Please use a mock zip archive instead.");
        archive.ExtractToDirectory(destinationDirectoryName, overwriteFiles);
    }

    public void Dispose()
    {
        archive.Dispose();
    }
}

public interface IZipArchiveEntry
{
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
    public Stream Open()
    => entry.Open();
    
    public void ExtractToFile(IFileSystem fileSystem, string destinationFileName)
    {
        // FileSystem is an argument only for this check, to prevent unwanted interaction with the real file system
        if (fileSystem is not FileSystem)
            throw new InvalidOperationException("Extracting a real zip archive in a mock file system is not supported. Please use a mock zip archive instead.");
        entry.ExtractToFile(destinationFileName);
    }
}
#pragma warning restore RS0030
