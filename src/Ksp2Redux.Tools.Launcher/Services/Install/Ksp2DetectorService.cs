using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public interface IKsp2DetectorService
{
    public string? DetectKsp2InstallLocation();
}

public class Ksp2DetectorService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider, IOperatingSystemService operatingSystemService, ILogService log)
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
        catch (Exception ex)
        {
            log.Warn($"Found {libraryFoldersVdf} but couldn't read it: {ex.Message}. Additional Steam libraries won't be checked.");
            yield break;
        }

        var matchCount = 0;
        foreach (Match match in LibraryPathRegex.Matches(content))
        {
            var path = match.Groups["path"].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(path))
            {
                matchCount++;
                yield return path;
            }
        }

        // A zero-match result here is indistinguishable from "just no extra libraries configured" to
        // the user - but it can also mean Steam had this file mid-write when we read it, which would
        // otherwise look identical to "KSP2 isn't installed" once detection comes up empty.
        if (matchCount == 0)
        {
            log.Warn($"{libraryFoldersVdf} exists but no library paths were parsed out of it. If KSP2 isn't detected, this file may have been read mid-write.");
        }
    }

    private string? ReadInstallDirFromManifest(string manifestPath)
    {
        string content;
        try
        {
            content = fileSystem.File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            log.Warn($"Found {manifestPath} but couldn't read it: {ex.Message}.");
            return null;
        }

        var match = InstallDirRegex.Match(content);
        if (!match.Success)
        {
            log.Warn($"{manifestPath} exists but its installdir couldn't be parsed out of it. If KSP2 isn't detected, this file may have been read mid-write.");
            return null;
        }
        return match.Groups["installdir"].Value;
    }

    private static readonly Regex LibraryPathRegex = new(
        "\"path\"\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InstallDirRegex = new(
        "\"installdir\"\\s*\"(?<installdir>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
