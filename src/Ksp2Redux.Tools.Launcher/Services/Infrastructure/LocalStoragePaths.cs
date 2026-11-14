using System.IO.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

public static class LocalStoragePaths
{
    public const string ReduxFolder = "Ksp2Redux";
    public const string LogsSubfolder = "logs";

    public static string GetLocalStorageDirectory(IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    {
        string? isolated = environmentProvider.GetEnvironmentVariable("KSP2REDUX_LAUNCHER_STORAGE");
        if (!string.IsNullOrWhiteSpace(isolated))
        {
            if (!fileSystem.Path.IsPathFullyQualified(isolated))
                throw new InvalidOperationException("KSP2REDUX_LAUNCHER_STORAGE must be an absolute directory.");
            return fileSystem.Path.GetFullPath(isolated);
        }
        var appdataPath = environmentProvider.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(appdataPath) 
            ? throw new InvalidOperationException("Local application data folder path is unavailable.") 
            : fileSystem.Path.Combine(appdataPath, ReduxFolder);
    }

    public static string GetLogsDirectory(IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    {
        return fileSystem.Path.Combine(GetLocalStorageDirectory(fileSystem, environmentProvider), LogsSubfolder);
    }
}
