using System;
using System.IO.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Services;

internal static class LocalStoragePaths
{
    public const string ReduxFolder = "Ksp2Redux";
    public const string LogsSubfolder = "logs";

    public static string GetLocalStorageDirectory(IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    {
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
