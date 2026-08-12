using System.IO.Abstractions;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Reads the download cache folder.
/// </summary>
public static class CacheFiles
{
    /// <summary>
    /// Lists the files in the cache folder, newest write first.
    /// </summary>
    /// <param name="context">The setup every command shares.</param>
    /// <param name="directory">The folder to read.</param>
    /// <returns>The files, or an empty list when the folder does not exist or cannot be read.</returns>
    public static IReadOnlyList<IFileInfo> In(CliContext context, string directory)
    {
        try
        {
            if (!context.FileSystem.Directory.Exists(directory))
            {
                return [];
            }

            return
            [
                .. context.FileSystem.Directory
                    .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(context.FileSystem.FileInfo.New)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
            ];
        }
        catch (Exception e)
        {
            context.LogService.Error($"Could not read the download cache at {directory}", e);
            return [];
        }
    }
}
