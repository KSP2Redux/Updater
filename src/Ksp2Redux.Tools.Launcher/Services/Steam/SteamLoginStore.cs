using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

/// <summary>
/// A persisted Steam login, used to sign in again without prompting the player.
/// </summary>
/// <param name="AccountName">The Steam account name the token belongs to.</param>
/// <param name="RefreshToken">The long-lived Steam refresh token. The password is never stored.</param>
/// <param name="GuardData">Steam Guard machine data, which lets Steam skip the email code next time.</param>
public sealed record SavedSteamLogin(string AccountName, string RefreshToken, string? GuardData);

public interface ISteamLoginStore
{
    /// <summary>Returns the saved login, or null when there is none or it cannot be read.</summary>
    SavedSteamLogin? Load();

    /// <summary>Replaces any saved login.</summary>
    void Save(SavedSteamLogin login);

    void Clear();
}

/// <summary>
/// Keeps the Steam refresh token in a file only the current user can read.
/// </summary>
// No OS keychain: the token is protected by owner-only permissions on macOS and Linux, and by the
// per-user LocalAppData folder on Windows.
public class SteamLoginStore(
    IFileSystem fileSystem,
    ILauncherConfigService launcherConfigService,
    IOperatingSystemService operatingSystemService,
    ILogService log) : ISteamLoginStore
{
    public const string FILE_NAME = "steam-login.json";

    private string FilePath => fileSystem.Path.Combine(launcherConfigService.GetLocalStorageDirectory(), FILE_NAME);

    public SavedSteamLogin? Load()
    {
        if (!fileSystem.File.Exists(FilePath)) return null;

        try
        {
            var login = JsonSerializer.Deserialize<SavedSteamLogin>(fileSystem.File.ReadAllText(FilePath));
            return string.IsNullOrWhiteSpace(login?.AccountName) || string.IsNullOrWhiteSpace(login.RefreshToken) ? null : login;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"Ignoring an unreadable saved Steam login: {ex.Message}");
            return null;
        }
    }

    public void Save(SavedSteamLogin login)
    {
        fileSystem.Directory.CreateDirectory(launcherConfigService.GetLocalStorageDirectory());

        // Created owner-only rather than tightened after writing, so the token is never briefly readable.
        // UnixCreateMode only applies to a new file, hence the delete.
        if (fileSystem.File.Exists(FilePath)) fileSystem.File.Delete(FilePath);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (operatingSystemService.IsMacOS() || operatingSystemService.IsLinux())
        {
#pragma warning disable CA1416
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
#pragma warning restore CA1416
        }

        using var stream = fileSystem.FileStream.New(FilePath, options);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(login));
    }

    public void Clear()
    {
        if (fileSystem.File.Exists(FilePath)) fileSystem.File.Delete(FilePath);
    }
}
