using System.IO.Abstractions;

namespace Ksp2Redux.Tools.Common.Patching;

public static class FileInformation
{
    public const string Ksp2X64Data = "KSP2_x64_Data";

    public static HashSet<string> IgnoreDirectories(IFileSystem fileSystem) =>
    [
        "PDLauncher",
        "BepInEx",
        "KSP2_x64_BackUpThisFolder_ButDontShipItWithYourGame",
        fileSystem.Path.Combine(Ksp2X64Data, "StreamingAssets", "aa")
    ];

    public static HashSet<string> IgnoreFiles(IFileSystem fileSystem) => new(
    [
        fileSystem.Path.Combine(Ksp2X64Data, "data.unity3d"),
        fileSystem.Path.Combine(Ksp2X64Data, "boot.config"),
        "Ksp2.log",
        "redux.log",
        "redux.log.old"
    ], StringComparer.OrdinalIgnoreCase);
}
