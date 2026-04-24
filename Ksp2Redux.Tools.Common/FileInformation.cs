namespace Ksp2Redux.Tools.Common;

public static class FileInformation
{
    public const string Executable = "KSP2_x64.exe";
    public const string CrashHandler = "UnityCrashHandler64.exe";
    public const string UnityPlayer = "UnityPlayer.dll";
    public const string WinPixEventRuntime = "WinPixEventRuntime.dll";

    public const string MonoBleedingEdge = "MonoBleedingEdge";
    public const string Ksp2X64Data = "KSP2_x64_Data";

    public static readonly string Managed = Path.Combine(Ksp2X64Data, "Managed");

    public static readonly List<string> CopyFolders = [Ksp2X64Data, MonoBleedingEdge];
    public static readonly List<string> CopyFiles = [Executable, CrashHandler, UnityPlayer, WinPixEventRuntime];

    public static readonly HashSet<string> IgnoreDirectories =
    [
        "PDLauncher",
        "BepInEx",
        Path.Combine(Ksp2X64Data, "StreamingAssets", "aa")
    ];

    public static readonly HashSet<string> IgnoreFiles =
    [
        Path.Combine(Ksp2X64Data, "data.unity3d"),
        "Ksp2.log",
        "redux.log",
        "redux.log.old",
    ];
}
