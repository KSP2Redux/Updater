using Mono.Cecil;
using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using Ksp2Redux.Tools.Launcher.Services;

namespace Ksp2Redux.Tools.Launcher.Models;

public class Ksp2Install
{
    private readonly IFileSystem _fileSystem;
    private readonly IModuleDefinitionService _moduleDefinitionService;

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

    private static string GetAssemblyCSharpRelativePath(IFileSystem fileSystem)
        => fileSystem.Path.Combine("KSP2_x64_Data", "Managed", "Assembly-CSharp.dll");
    
    // Exists in Steam based installations, but doesn't exist in portable version.
    private const string SteamworksText = "KSP2_x64_Data/Plugins/Steamworks.NET.txt";
    private const string EpicGamesMarker = ".egstore";
    private const string PrepatchMarker = "prepatched.nodelete";
    
    public Ksp2Install(IFileSystem fileSystem, IModuleDefinitionService moduleDefinitionService, string exePath)
    {
        _fileSystem = fileSystem;
        _moduleDefinitionService = moduleDefinitionService;
        ExePath = exePath;

        IsValid = _fileSystem.File.Exists(exePath) && _fileSystem.Path.GetFileName(exePath) == KSP2_EXE_NAME;
        if (IsValid)
        {
            InstallDir = _fileSystem.Path.GetDirectoryName(exePath)!;
            var isSteam = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, SteamworksText));
            var isEpic = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, EpicGamesMarker));
            var isRedux = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, "Redux"));
            var isPrepatch = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, PrepatchMarker));
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

    private GameVersion? TryGetGameVersionFromMainAssembly(string installDir, bool isRedux)
    {
        try
        {
            var mainAssembly = _fileSystem.Path.Combine(installDir, GetAssemblyCSharpRelativePath(_fileSystem));
            var module = _moduleDefinitionService.ReadModule(mainAssembly);
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
