using System.IO.Abstractions;
using System.IO.Compression;

namespace Ksp2Redux.Tools.Common.Service;

public interface IZipFileService
{
    IZipArchive OpenRead(string archiveFileName);
    void ExtractToDirectory(IZipArchive source, string destinationDirectoryName, bool overwriteFiles);
}

public class ZipFileService(IFileSystem fileSystem) : IZipFileService
{
#pragma warning disable RS0030
    
    public IZipArchive OpenRead(string archiveFileName)
        => new ZipArchiveWrapper(ZipFile.OpenRead(archiveFileName));
    
    public void ExtractToDirectory(IZipArchive source, string destinationDirectoryName, bool overwriteFiles)
        => source.ExtractToDirectory(fileSystem, destinationDirectoryName, overwriteFiles);
    
#pragma warning restore RS0030
}