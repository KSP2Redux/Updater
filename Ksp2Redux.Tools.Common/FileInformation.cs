namespace Ksp2Redux.Tools.Common;

public static class FileInformation
{
    public const string Executable = "KSP2_x64.exe";
    public const string CrashHandler = "UnityCrashHandler64.exe";
    public const string UnityPlayer = "UnityPlayer.dll";
    public const string WinPixEventRuntime = "WinPixEventRuntime.dll";

    public const string MonoBleedingEdge = "MonoBleedingEdge";
    public const string Ksp2X64Data = "KSP2_x64_Data";

    public const string Managed = $"{Ksp2X64Data}\\Managed";

    public static List<string> CopyFolders = [Ksp2X64Data, MonoBleedingEdge];
    public static List<string> CopyFiles = [Executable, CrashHandler, UnityPlayer, WinPixEventRuntime];

    public static HashSet<string> IgnoreDirectories = [$"{Ksp2X64Data}\\Resources", "PDLauncher", "BepInEx", $@"{Ksp2X64Data}\StreamingAssets\aa"];

    public static HashSet<string> IgnoreFiles =
    [
        $"{Ksp2X64Data}\\data.unity3d",
        "Ksp2.log",
        "redux.log",
        "redux.log.old",
    ];
}
