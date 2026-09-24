using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public enum Ksp2GameRemovalKind
{
    /// <summary>The folder is a KSP2 install the launcher can delete itself.</summary>
    DeleteFolder,

    /// <summary>The folder belongs to a Steam library, so Steam has to uninstall it or it goes on thinking it is installed.</summary>
    UninstallThroughSteam,

    /// <summary>The folder is already gone.</summary>
    Missing,

    /// <summary>The folder does not look like a KSP2 install, so nothing may be deleted.</summary>
    NotAGameFolder
}

/// <param name="Kind">How the game can be removed.</param>
/// <param name="Folder">The install folder, the one holding KSP2_x64.exe.</param>
public sealed record Ksp2GameRemoval(Ksp2GameRemovalKind Kind, string Folder);

public interface IKsp2GameUninstallService
{
    /// <summary>
    /// Works out how the game at <paramref name="exePath"/> can be removed, without changing anything.
    /// </summary>
    Ksp2GameRemoval Inspect(string exePath);

    /// <summary>
    /// Deletes the install folder of a <see cref="Ksp2GameRemovalKind.DeleteFolder"/> removal.
    /// </summary>
    /// <exception cref="InvalidOperationException">The removal is of any other kind.</exception>
    Task DeleteAsync(Ksp2GameRemoval removal);
}

/// <summary>
/// Removes a KSP2 install from disk. Saves and settings live in the game data folder, not the install
/// folder, so they survive it.
/// </summary>
public class Ksp2GameUninstallService(IFileSystem fileSystem, ILogService log) : IKsp2GameUninstallService
{
    private const string DATA_FOLDER_NAME = "KSP2_x64_Data";
    private const string STEAM_APP_MANIFEST = "appmanifest_954850.acf";

    public Ksp2GameRemoval Inspect(string exePath)
    {
        var folder = string.IsNullOrWhiteSpace(exePath) ? null : fileSystem.Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(folder) || !fileSystem.Directory.Exists(folder))
        {
            return new Ksp2GameRemoval(Ksp2GameRemovalKind.Missing, folder ?? string.Empty);
        }

        // A recursive delete of whatever folder a profile points at is only safe when that folder is
        // unmistakably the game. Someone pointing a profile at an exe dropped in their Downloads folder
        // must not lose the rest of Downloads.
        if (!fileSystem.File.Exists(fileSystem.Path.Combine(folder, Ksp2Install.KSP2_EXE_NAME)) ||
            !fileSystem.Directory.Exists(fileSystem.Path.Combine(folder, DATA_FOLDER_NAME)))
        {
            return new Ksp2GameRemoval(Ksp2GameRemovalKind.NotAGameFolder, folder);
        }

        return new Ksp2GameRemoval(IsInSteamLibrary(folder) ? Ksp2GameRemovalKind.UninstallThroughSteam : Ksp2GameRemovalKind.DeleteFolder, folder);
    }

    public async Task DeleteAsync(Ksp2GameRemoval removal)
    {
        if (removal.Kind != Ksp2GameRemovalKind.DeleteFolder)
        {
            throw new InvalidOperationException($"A {removal.Kind} removal cannot be deleted by the launcher.");
        }

        log.Info($"Deleting KSP2 install at {removal.Folder}.");
        await Task.Run(() =>
        {
            // Directory.Delete refuses read-only files on Windows, and some game files ship read-only.
            foreach (var file in fileSystem.Directory.EnumerateFiles(removal.Folder, "*", SearchOption.AllDirectories))
            {
                var attributes = fileSystem.File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    fileSystem.File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            fileSystem.Directory.Delete(removal.Folder, recursive: true);
        });
        log.Info($"Deleted KSP2 install at {removal.Folder}.");
    }

    private bool IsInSteamLibrary(string folder)
    {
        var common = fileSystem.Path.GetDirectoryName(folder);
        var steamapps = string.IsNullOrEmpty(common) ? null : fileSystem.Path.GetDirectoryName(common);
        if (string.IsNullOrEmpty(common) || string.IsNullOrEmpty(steamapps)) return false;

        return string.Equals(fileSystem.Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(fileSystem.Path.GetFileName(steamapps), "steamapps", StringComparison.OrdinalIgnoreCase) &&
               fileSystem.File.Exists(fileSystem.Path.Combine(steamapps, STEAM_APP_MANIFEST));
    }
}
