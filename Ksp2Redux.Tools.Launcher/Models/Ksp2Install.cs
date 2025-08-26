using System;
using System.IO;
using System.Reflection;

namespace Ksp2Redux.Tools.Launcher.Models;

public class Ksp2Install
{
    /// <summary>
    /// If the path pointed to represents a valid KSP2 installation or not.
    /// </summary>
    public bool IsValid { get; }

    public GameVersion? GameVersion { get; private set; }

    private readonly string exePath;
    private readonly string installDir;

    private const string KSP2_EXE_NAME = "KSP2_x64.exe";
    private static readonly string assemblyCSharpRelativePath = Path.Combine("KSP2_x64_Data", "Managed", "Assembly-CSharp.dll");

    public Ksp2Install(string exePath)
    {
        this.exePath = exePath;
        IsValid = File.Exists(exePath) && Path.GetFileName(exePath) == KSP2_EXE_NAME;
        if (IsValid)
        {
            installDir = Path.GetDirectoryName(exePath)!;
            GameVersion = TryGetGameVersionFromMainAssembly(installDir);
        }
        else
        {
            installDir = "";
        }
    }

    private static GameVersion? TryGetGameVersionFromMainAssembly(string installDir)
    {
        var mainAssembly = Path.Combine(installDir, assemblyCSharpRelativePath);
        var asm = Assembly.LoadFile(mainAssembly);
        Type? versionType = asm.GetType("VersionID");
        if (versionType is not null)
        {
            return GameVersion.FromVersionIDType(versionType);
        }
        return null;
    }
}
