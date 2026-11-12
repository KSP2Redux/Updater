// The .NET build takes IFileSystem from TestableIO.System.IO.Abstractions (through Testably.Abstractions),
// which the Unity editor does not have. This is the slice of that interface the converter uses, with the
// same member names, on the real file system. The csproj leaves this folder out of the .NET build.
namespace System.IO.Abstractions;

/// <summary>
/// The file system the converter reads and writes through.
/// </summary>
public interface IFileSystem
{
    /// <summary>Gets the file operations.</summary>
    IFile File { get; }

    /// <summary>Gets the directory operations.</summary>
    IDirectory Directory { get; }

    /// <summary>Gets the path operations.</summary>
    IPath Path { get; }

    /// <summary>Gets the file stream factory.</summary>
    IFileStreamFactory FileStream { get; }

    /// <summary>Gets the file information factory.</summary>
    IFileInfoFactory FileInfo { get; }
}

/// <summary>File operations, as <see cref="System.IO.File" /> has them.</summary>
public interface IFile
{
    /// <summary>Checks whether a file exists.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>True if the file exists, false otherwise.</returns>
    bool Exists(string path);

    /// <summary>Deletes a file. A missing file is not an error.</summary>
    /// <param name="path">The file path.</param>
    void Delete(string path);

    /// <summary>Moves a file.</summary>
    /// <param name="sourceFileName">The file to move.</param>
    /// <param name="destFileName">Its new path.</param>
    void Move(string sourceFileName, string destFileName);

    /// <summary>Reads a whole file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The file's bytes.</returns>
    byte[] ReadAllBytes(string path);

    /// <summary>Writes a whole file, replacing any existing one.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="bytes">The bytes to write.</param>
    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>Replaces a file with another in one step.</summary>
    /// <param name="sourceFileName">The new file.</param>
    /// <param name="destinationFileName">The file it replaces.</param>
    /// <param name="destinationBackupFileName">Where the replaced file goes, or null to discard it.</param>
    void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName);
}

/// <summary>Directory operations, as <see cref="System.IO.Directory" /> has them.</summary>
public interface IDirectory
{
    /// <summary>Checks whether a directory exists.</summary>
    /// <param name="path">The directory path.</param>
    /// <returns>True if the directory exists, false otherwise.</returns>
    bool Exists(string path);

    /// <summary>Creates a directory and its parents.</summary>
    /// <param name="path">The directory path.</param>
    /// <returns>The directory.</returns>
    // Nullable because Unity's base library has no nullable annotations, so a string.IsNullOrEmpty check before
    // the call does not prove the path non-null to the compiler there.
    DirectoryInfo CreateDirectory(string? path);

    /// <summary>Deletes an empty directory.</summary>
    /// <param name="path">The directory path.</param>
    void Delete(string path);

    /// <summary>Lists the files and directories in a directory.</summary>
    /// <param name="path">The directory path.</param>
    /// <returns>Their paths.</returns>
    IEnumerable<string> EnumerateFileSystemEntries(string path);

    /// <summary>Lists the files in a directory that match a pattern.</summary>
    /// <param name="path">The directory path.</param>
    /// <param name="searchPattern">The file name pattern.</param>
    /// <param name="searchOption">Whether to include subdirectories.</param>
    /// <returns>Their paths.</returns>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
}

/// <summary>Path operations, as <see cref="System.IO.Path" /> has them.</summary>
public interface IPath
{
    /// <summary>Joins two path parts.</summary>
    /// <param name="path1">The first part.</param>
    /// <param name="path2">The second part.</param>
    /// <returns>The joined path.</returns>
    string Combine(string path1, string path2);

    /// <summary>Gets the directory part of a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The directory, or null for a root.</returns>
    string? GetDirectoryName(string path);

    /// <summary>Gets the file name part of a path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The file name.</returns>
    string GetFileName(string path);

    /// <summary>Makes a path absolute.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The absolute path.</returns>
    string GetFullPath(string path);
}

/// <summary>Opens file streams.</summary>
public interface IFileStreamFactory
{
    /// <summary>Opens a file stream.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">How to open or create the file.</param>
    /// <param name="access">The access the stream has.</param>
    /// <param name="share">The access other streams may have.</param>
    /// <returns>The stream.</returns>
    Stream New(string path, FileMode mode, FileAccess access, FileShare share);

    /// <summary>Opens a file stream with a buffer size.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">How to open or create the file.</param>
    /// <param name="access">The access the stream has.</param>
    /// <param name="share">The access other streams may have.</param>
    /// <param name="bufferSize">The buffer size in bytes.</param>
    /// <returns>The stream.</returns>
    Stream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize);

    /// <summary>Opens a file stream with a buffer size and access hints.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">How to open or create the file.</param>
    /// <param name="access">The access the stream has.</param>
    /// <param name="share">The access other streams may have.</param>
    /// <param name="bufferSize">The buffer size in bytes.</param>
    /// <param name="options">How the file will be read.</param>
    /// <returns>The stream.</returns>
    Stream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options);
}

/// <summary>Creates file information objects.</summary>
public interface IFileInfoFactory
{
    /// <summary>Gets the information of a file.</summary>
    /// <param name="fileName">The file path.</param>
    /// <returns>The file information.</returns>
    FileInfo New(string fileName);
}

/// <summary>
/// The real file system behind <see cref="IFileSystem" />.
/// </summary>
public sealed class FileSystem : IFileSystem, IFile, IDirectory, IPath, IFileStreamFactory, IFileInfoFactory
{
    /// <inheritdoc />
    public IFile File => this;

    /// <inheritdoc />
    public IDirectory Directory => this;

    /// <inheritdoc />
    public IPath Path => this;

    /// <inheritdoc />
    public IFileStreamFactory FileStream => this;

    /// <inheritdoc />
    public IFileInfoFactory FileInfo => this;

    bool IFile.Exists(string path) => System.IO.File.Exists(path);

    void IFile.Delete(string path) => System.IO.File.Delete(path);

    void IFile.Move(string sourceFileName, string destFileName) => System.IO.File.Move(sourceFileName, destFileName);

    byte[] IFile.ReadAllBytes(string path) => System.IO.File.ReadAllBytes(path);

    void IFile.WriteAllBytes(string path, byte[] bytes) => System.IO.File.WriteAllBytes(path, bytes);

    void IFile.Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName) =>
        System.IO.File.Replace(sourceFileName, destinationFileName, destinationBackupFileName);

    bool IDirectory.Exists(string path) => System.IO.Directory.Exists(path);

    DirectoryInfo IDirectory.CreateDirectory(string? path) => System.IO.Directory.CreateDirectory(path);

    void IDirectory.Delete(string path) => System.IO.Directory.Delete(path);

    IEnumerable<string> IDirectory.EnumerateFileSystemEntries(string path) => System.IO.Directory.EnumerateFileSystemEntries(path);

    IEnumerable<string> IDirectory.EnumerateFiles(string path, string searchPattern, SearchOption searchOption) =>
        System.IO.Directory.EnumerateFiles(path, searchPattern, searchOption);

    string IPath.Combine(string path1, string path2) => System.IO.Path.Combine(path1, path2);

    string? IPath.GetDirectoryName(string path) => System.IO.Path.GetDirectoryName(path);

    string IPath.GetFileName(string path) => System.IO.Path.GetFileName(path);

    string IPath.GetFullPath(string path) => System.IO.Path.GetFullPath(path);

    Stream IFileStreamFactory.New(string path, FileMode mode, FileAccess access, FileShare share) =>
        new System.IO.FileStream(path, mode, access, share);

    Stream IFileStreamFactory.New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) =>
        new System.IO.FileStream(path, mode, access, share, bufferSize);

    Stream IFileStreamFactory.New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) =>
        new System.IO.FileStream(path, mode, access, share, bufferSize, options);

    FileInfo IFileInfoFactory.New(string fileName) => new(fileName);
}