using System.IO.Abstractions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;
using Ksp2Redux.Tools.Launcher.Views.Steam;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

public interface ISteamDialogService
{
    /// <summary>
    /// Shows the "Sign in with Steam" window.
    /// </summary>
    /// <returns>True when the player signed in.</returns>
    Task<bool> ShowSignInAsync();

    /// <summary>
    /// Asks where to put the game, then downloads it from Steam and adds it as an install, signing in first
    /// when needed.
    /// </summary>
    /// <returns>The new install's exe path, or null when the player cancelled.</returns>
    Task<string?> DownloadGameAsync();
}

/// <summary>
/// Opens the Steam windows over the launcher's main window.
/// </summary>
public class SteamDialogService(
    ISteamSessionService session,
    ISteamDepotDownloader downloader,
    IKsp2InstallService installService,
    IFileSystem fileSystem,
    IEnvironmentProvider environmentProvider,
    ILogService log) : ISteamDialogService
{
    private static Window? OwnerWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<bool> ShowSignInAsync()
    {
        if (OwnerWindow is not { } owner) return false;

        var window = new SteamSignInWindow(new SteamSignInViewModel(session, log));
        return await window.ShowDialog<bool>(owner);
    }

    public async Task<string?> DownloadGameAsync()
    {
        if (OwnerWindow is not { } owner) return null;

        if (!session.IsSignedIn && !await session.TryResumeAsync(CancellationToken.None) && !await ShowSignInAsync())
        {
            return null;
        }

        var target = await PickTargetFolderAsync(owner);
        if (target is null) return null;

        var window = new SteamDownloadWindow(new SteamDownloadViewModel(target, downloader, installService, fileSystem, log));
        return await window.ShowDialog<string?>(owner);
    }

    // Offers ~/Games (or the user profile's Games folder on Windows) as the starting point.
    private async Task<string?> PickTargetFolderAsync(Window owner)
    {
        var games = fileSystem.Path.Combine(environmentProvider.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
        fileSystem.Directory.CreateDirectory(games);

        var start = await owner.StorageProvider.TryGetFolderFromPathAsync(games);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to download Kerbal Space Program 2",
            AllowMultiple = false,
            SuggestedStartLocation = start
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } chosen) return null;

        return SteamDepotDownloader.GameFolderIn(fileSystem, chosen);
    }
}
