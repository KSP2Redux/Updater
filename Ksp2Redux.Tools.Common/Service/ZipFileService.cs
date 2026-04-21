using System.IO.Compression;

namespace Ksp2Redux.Tools.Common.Service;

public interface IZipFileService
{
    ZipArchive OpenRead(string archiveFileName);
    void ExtractToDirectory(ZipArchive source, string destinationDirectoryName, bool overwriteFiles);
}

public class ZipFileService : IZipFileService
{
#pragma warning disable RS0030
    
    public ZipArchive OpenRead(string archiveFileName)
        => ZipFile.OpenRead(archiveFileName);
    
    public void ExtractToDirectory(ZipArchive source, string destinationDirectoryName, bool overwriteFiles)
        => source.ExtractToDirectory(destinationDirectoryName, overwriteFiles);
    
#pragma warning restore RS0030
}