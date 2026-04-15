using Mono.Cecil;
using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;

namespace Ksp2Redux.Tools.Launcher.Models;

public class Ksp2Install
{
    /// <summary>
    /// If the path pointed to represents a valid KSP2 installation or not.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// The distribution of the current install
    /// </summary>
    public Distribution Distribution { get; }

    /// <summary>
    /// Game version deteced from reflection.
    /// </summary>
    public GameVersion? GameVersion { get; private set; }

    public string InstallDir { get; }

    public string ExePath { get; }

    public const string KSP2_EXE_NAME = "KSP2_x64.exe";
    private static readonly string AssemblyCSharpRelativePath = Path.Combine("KSP2_x64_Data", "Managed", "Assembly-CSharp.dll");
    // Exists in Steam based installations, but doesn't exist in portable version.
    private const string SteamworksText = "KSP2_x64_Data/Plugins/Steamworks.NET.txt";
    private const string EpicGamesMarker = ".egstore";
    private const string PrepatchMarker = "prepatched.nodelete";
    
    public Ksp2Install(IFileSystem fileSystem, string exePath)
    {
        ExePath = exePath;

        IsValid = fileSystem.File.Exists(exePath) && Path.GetFileName(exePath) == KSP2_EXE_NAME;
        if (IsValid)
        {
            InstallDir = Path.GetDirectoryName(exePath)!;
            var isSteam = Path.Exists(Path.Combine(InstallDir, SteamworksText));
            var isEpic = Path.Exists(Path.Combine(InstallDir, EpicGamesMarker));
            var isRedux = Path.Exists(Path.Combine(InstallDir, "Redux"));
            var isPrepatch = Path.Exists(Path.Combine(InstallDir, PrepatchMarker));
            Distribution = isRedux    ? Distribution.Redux   :
                           isPrepatch ? Distribution.Prepatched :
                           isSteam    ? Distribution.Steam   :
                           isEpic     ? Distribution.Epic    : 
                                        Distribution.Portable;
            
            GameVersion = TryGetGameVersionFromMainAssembly(InstallDir,Distribution == Distribution.Redux);
        }
        else
        {
            InstallDir = "";
        }
    }

    private static GameVersion? TryGetGameVersionFromMainAssembly(string installDir, bool isRedux)
    {
        try
        {
            var mainAssembly = Path.Combine(installDir, AssemblyCSharpRelativePath);
            var module = ModuleDefinition.ReadModule(mainAssembly);
            var versionType = module.Types.First(t => t.Name == "VersionID");
            if (versionType is not null)
            {
                var gameVersionFound = GameVersion.FromVersionIDType(versionType, isRedux);
                module.Dispose();
                return gameVersionFound;
            }

            module.Dispose();
            return null;
        }
        catch (Exception e)
        {
            return null;
        }
    }

    public override string ToString()
    {
        return $"Ksp2Install: IsValid:{IsValid} Distribution:{Distribution} GameVersion:{GameVersion} Dir:\"{InstallDir}\"";
    }
}
