using System;
using System.IO.Abstractions;
using System.Threading.Tasks;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IKsp2DetectorService
{
    public string? DetectKsp2InstallLocation();
}

public class Ksp2DetectorService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    : IKsp2DetectorService
{
    private static readonly string[] StaticInstallLocations =
    [
        @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe",
        @"D:\SteamLibrary\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe",
        @"E:\SteamLibrary\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe",
        @"F:\SteamLibrary\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe",
        @"G:\SteamLibrary\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe",
        @"C:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"D:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"E:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"F:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe",
        @"G:\Program Files\Epic Games\KerbalSpaceProgram2\KSP2_x64.exe"
    ];

    public string? DetectKsp2InstallLocation()
    {
        foreach (var file in StaticInstallLocations)
        {
            if (fileSystem.File.Exists(file))
            {
                return file;
            }
        }
        
        var privateDivisionPath = fileSystem.Path.Combine(
            environmentProvider.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs",
            "Kerbal Space Program 2", "KSP2_x64.exe");

        return fileSystem.File.Exists(privateDivisionPath) ? privateDivisionPath : null;
    }
}