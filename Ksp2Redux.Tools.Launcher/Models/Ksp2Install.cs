using Mono.Cecil;
using System;
using System.IO;
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
    /// True if the installation must be patched with a steam patch. False otherwise.
    /// </summary>
    public bool IsSteam { get; }

    /// <summary>
    /// True if a redux patch (of any version) has been installed.
    /// </summary>
    public bool IsRedux { get; }

    /// <summary>
    /// Game version deteced from reflection.
    /// </summary>
    public GameVersion? GameVersion { get; private set; }

    public string InstallDir { get; }

    public string ExePath { get; }

    private const string KSP2_EXE_NAME = "KSP2_x64.exe";
    private static readonly string assemblyCSharpRelativePath = Path.Combine("KSP2_x64_Data", "Managed", "Assembly-CSharp.dll");
    // Exists in Steam based installations, but doesn't exist in portable version.
    private static readonly string steamworksText = Path.Combine("KSP2_x64_Data", "Plugins", "Steamworks.NET.txt");

    public Ksp2Install(string exePath)
    {
        ExePath = exePath;
        IsValid = File.Exists(exePath) && Path.GetFileName(exePath) == KSP2_EXE_NAME;
        if (IsValid)
        {
            InstallDir = Path.GetDirectoryName(exePath)!;
            GameVersion = TryGetGameVersionFromMainAssembly(InstallDir);
            IsSteam = Path.Exists(Path.Combine(InstallDir, steamworksText));
            IsRedux = Path.Exists(Path.Combine(InstallDir, "Redux"));
        }
        else
        {
            InstallDir = "";
        }
    }

    private static GameVersion? TryGetGameVersionFromMainAssembly(string installDir)
    {
        var mainAssembly = Path.Combine(installDir, assemblyCSharpRelativePath);
        var module = ModuleDefinition.ReadModule(mainAssembly);
        var versionType = module.Types.Where(t => t.Name == "VersionID").First();
        if (versionType is not null)
        {
            return GameVersion.FromVersionIDType(versionType);
        }
        return null;
    }

    public override string ToString()
    {
        return $"Ksp2Install: IsValid:{IsValid} IsSteam:{IsSteam} IsRedux:{IsRedux} GameVersion:{GameVersion} Dir:\"{InstallDir}\"";
    }
}
