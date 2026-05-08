using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Text.RegularExpressions;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IKsp2DetectorService
{
    public string? DetectKsp2InstallLocation();
}

public class Ksp2DetectorService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider, IOperatingSystemService operatingSystemService)
    : IKsp2DetectorService
{
    private const string Ksp2SteamAppId = "954850";
    private const string Ksp2ExeName = "KSP2_x64.exe";

    private static readonly string[] WindowsEpicLocations =
    [
        @"C:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"D:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"E:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"F:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"G:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe"
    ];

    private static readonly string[] WindowsSteamRoots =
    [
        @"C:\Program Files (x86)\Steam",
        @"C:\Program Files\Steam",
        @"D:\Steam",
        @"E:\Steam",
        @"F:\Steam",
        @"G:\Steam"
    ];

    private static readonly string[] LinuxSteamRootRelativePaths =
    [
        ".steam/steam",
        ".steam/root",
        ".steam/debian-installation",
        ".local/share/Steam",
        ".var/app/com.valvesoftware.Steam/data/Steam"
    ];

    public string? DetectKsp2InstallLocation()
    {
        // TODO: remove temp code
        Console.WriteLine($"TEMP: linux: {operatingSystemService.IsLinux()}");
        
        if (operatingSystemService.IsLinux())
        {
            return DetectSteamInstall(EnumerateLinuxSteamRoots());
        }

        return DetectSteamInstall(WindowsSteamRoots) ?? DetectWindowsNonSteam();
    }

    private IEnumerable<string> EnumerateLinuxSteamRoots()
    {
        var home = environmentProvider.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;

        foreach (var rootRelative in LinuxSteamRootRelativePaths)
        {
            yield return fileSystem.Path.Combine(home, rootRelative);
        }
    }

    private string? DetectSteamInstall(IEnumerable<string> steamRoots)
    {
        foreach (var steamRoot in steamRoots)
        {
            if (!fileSystem.Directory.Exists(steamRoot)) continue;

            // TODO: remove temp code
            Console.WriteLine($"TEMP: directories: {steamRoot}");
            
            foreach (var libraryPath in ReadLibraryFolders(steamRoot))
            {
                var steamapps = fileSystem.Path.Combine(libraryPath, "steamapps");
                var manifest = fileSystem.Path.Combine(steamapps, $"appmanifest_{Ksp2SteamAppId}.acf");
                if (!fileSystem.File.Exists(manifest)) continue;

                var installDir = ReadInstallDirFromManifest(manifest);
                if (string.IsNullOrWhiteSpace(installDir)) continue;

                var candidate = fileSystem.Path.Combine(steamapps, "common", installDir, Ksp2ExeName);
                if (fileSystem.File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string? DetectWindowsNonSteam()
    {
        foreach (var file in WindowsEpicLocations)
        {
            if (fileSystem.File.Exists(file))
            {
                return file;
            }
        }

        var privateDivisionPath = fileSystem.Path.Combine(
            environmentProvider.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs",
            "Kerbal Space Program 2", Ksp2ExeName);

        return fileSystem.File.Exists(privateDivisionPath) ? privateDivisionPath : null;
    }

    private IEnumerable<string> ReadLibraryFolders(string steamRoot)
    {
        yield return steamRoot;

        var libraryFoldersVdf = fileSystem.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!fileSystem.File.Exists(libraryFoldersVdf)) yield break;

        string content;
        try
        {
            content = fileSystem.File.ReadAllText(libraryFoldersVdf);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in LibraryPathRegex.Matches(content))
        {
            var path = match.Groups["path"].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private string? ReadInstallDirFromManifest(string manifestPath)
    {
        string content;
        try
        {
            content = fileSystem.File.ReadAllText(manifestPath);
        }
        catch
        {
            return null;
        }

        var match = InstallDirRegex.Match(content);
        return match.Success ? match.Groups["installdir"].Value : null;
    }

    private static readonly Regex LibraryPathRegex = new(
        "\"path\"\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstallDirRegex = new(
        "\"installdir\"\\s*\"(?<installdir>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
