using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

/// <summary>
/// What is kept between launcher sessions so the player does not sign in every time.
/// </summary>
/// <param name="AccountName">The Steam account name the token belongs to.</param>
/// <param name="RefreshToken">The long-lived Steam refresh token. The password is never stored.</param>
/// <param name="GuardData">Steam Guard machine data, which lets Steam skip the email code next time.</param>
public sealed record SavedSteamLogin(string AccountName, string RefreshToken, string? GuardData);

public interface ISteamLoginStore
{
    SavedSteamLogin? Load();

    void Save(SavedSteamLogin login);

    void Clear();
}

/// <summary>
/// Keeps the Steam refresh token in a file only the current user can read.
/// </summary>
// The launcher has no OS keychain integration yet, so the token sits in the launcher's storage folder
// with owner-only permissions on macOS and Linux. On Windows the folder is already per-user under
// LocalAppData. A refresh token only grants access to this account's Steam session, and signing out
// deletes it.
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

        // The file is created with owner-only permissions rather than tightened after writing, so the
        // token is never readable by other users, not even for a moment. The create mode only applies
        // to a new file, hence deleting any old one first.
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
