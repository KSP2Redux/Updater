namespace Ksp2Redux.Tools.Common;

public static class CacheRestore
{
    public static void RecursivelyRestoreCache(string directory, string cache)
    {
        var directoryInfo = new DirectoryInfo(directory);
        List<string> toDelete = [];
        List<string> toRestore = [];
        foreach (var file in directoryInfo.GetFiles())
        {
            if (file.Name.EndsWith($".{cache}.r"))
            {
                // Delete the cached file
                toDelete.Add(file.FullName);
                // And the file it refers to
                toDelete.Add(file.FullName[..^$".{cache}.r".Length]);
            }
            else if (file.Name.EndsWith($".{cache}"))
            {
                toDelete.Add(file.FullName);
                toRestore.Add(file.FullName[..^$".{cache}".Length]);
            }
        }

        foreach (var file in toRestore)
        {
            File.Copy($"{file}.{cache}", file, true);
        }

        foreach (var file in toDelete)
        {
            File.Delete(file);
        }
    }
}