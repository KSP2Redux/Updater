using System.IO.Abstractions;
using System.IO.Compression;
using Ksp2Redux.Tools.Common.Wrappers;

namespace Ksp2Redux.Tools.Common.Services;

public interface IZipFileService
{
    IZipArchive OpenRead(string archiveFileName);
    IZipArchive NewArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen);
    void ExtractToDirectory(IZipArchive source, string destinationDirectoryName, bool overwriteFiles);
}

public class ZipFileService(IFileSystem fileSystem) : IZipFileService
{
#pragma warning disable RS0030
    
    public IZipArchive OpenRead(string archiveFileName)
        => new ZipArchiveWrapper(ZipFile.OpenRead(archiveFileName));
    
    public IZipArchive NewArchive(Stream stream, ZipArchiveMode mode, bool leaveOpen)
        => new ZipArchiveWrapper(new ZipArchive(stream, mode, leaveOpen));
    
    public void ExtractToDirectory(IZipArchive source, string destinationDirectoryName, bool overwriteFiles)
        => source.ExtractToDirectory(fileSystem, destinationDirectoryName, overwriteFiles);
    
#pragma warning restore RS0030
}