using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

/// <summary>
/// A persisted Steam login, used to sign in again without prompting the player. The password is never stored.
/// </summary>
/// <param name="AccountName">The Steam account name the token belongs to.</param>
/// <param name="RefreshToken">The long-lived Steam refresh token.</param>
/// <param name="GuardData">Steam Guard machine data, which lets Steam skip the email code next time.</param>
public sealed record SavedSteamLogin(string AccountName, string RefreshToken, string? GuardData);

public interface ISteamLoginStore
{
    /// <summary>Returns the saved login, or null when there is none or it cannot be read.</summary>
    SavedSteamLogin? Load();

    /// <summary>Replaces any saved login.</summary>
    void Save(SavedSteamLogin login);

    /// <summary>Forgets the saved login.</summary>
    void Clear();
}

/// <summary>
/// Keeps the Steam login in the operating system's secret store: DPAPI on Windows and the keychain on macOS.
/// Linux, and any platform whose store fails, gets a file only the current user can read.
/// </summary>
public class SteamLoginStore : ISteamLoginStore
{
    public const string FILE_NAME = "steam-login.json";
    public const string PROTECTED_FILE_NAME = "steam-login.dat";

    private readonly IFileSystem _fileSystem;
    private readonly ILauncherConfigService _launcherConfigService;
    private readonly IOperatingSystemService _operatingSystemService;
    private readonly ILogService _log;
    private readonly ISteamLoginVault? _vault;

    public SteamLoginStore(IFileSystem fileSystem, ILauncherConfigService launcherConfigService,
        IOperatingSystemService operatingSystemService, ILogService log)
        : this(fileSystem, launcherConfigService, operatingSystemService, log, null)
    {
#pragma warning disable CA1416
        if (operatingSystemService.IsWindows())
        {
            _vault = new DpapiSteamLoginVault(fileSystem, fileSystem.Path.Combine(launcherConfigService.GetLocalStorageDirectory(), PROTECTED_FILE_NAME));
        }
        else if (operatingSystemService.IsMacOS())
        {
            _vault = new KeychainSteamLoginVault();
        }
#pragma warning restore CA1416
    }

    internal SteamLoginStore(IFileSystem fileSystem, ILauncherConfigService launcherConfigService,
        IOperatingSystemService operatingSystemService, ILogService log, ISteamLoginVault? vault)
    {
        _fileSystem = fileSystem;
        _launcherConfigService = launcherConfigService;
        _operatingSystemService = operatingSystemService;
        _log = log;
        _vault = vault;
    }

    private string FilePath => _fileSystem.Path.Combine(_launcherConfigService.GetLocalStorageDirectory(), FILE_NAME);

    public SavedSteamLogin? Load()
    {
        if (_vault is not null)
        {
            try
            {
                if (_vault.Read() is { } stored) return Parse(stored);
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                _log.Warn($"Couldn't read the saved Steam login from {_vault.Name}: {ex.Message}");
            }
        }

        if (!_fileSystem.File.Exists(FilePath)) return null;

        string plain;
        try
        {
            plain = _fileSystem.File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Ignoring an unreadable saved Steam login: {ex.Message}");
            return null;
        }

        var login = Parse(plain);
        if (login is not null && _vault is not null && TryWriteVault(plain))
        {
            _fileSystem.File.Delete(FilePath);
        }

        return login;
    }

    public void Save(SavedSteamLogin login)
    {
        var payload = JsonSerializer.Serialize(login);
        _fileSystem.Directory.CreateDirectory(_launcherConfigService.GetLocalStorageDirectory());

        if (_vault is not null && TryWriteVault(payload))
        {
            if (_fileSystem.File.Exists(FilePath)) _fileSystem.File.Delete(FilePath);
            return;
        }

        WriteOwnerOnlyFile(payload);
    }

    public void Clear()
    {
        try
        {
            _vault?.Delete();
        }
        catch (IOException ex)
        {
            _log.Warn($"Couldn't remove the Steam login from {_vault!.Name}: {ex.Message}");
        }

        if (_fileSystem.File.Exists(FilePath)) _fileSystem.File.Delete(FilePath);
    }

    private bool TryWriteVault(string payload)
    {
        try
        {
            _vault!.Write(payload);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _log.Warn($"Couldn't save the Steam login in {_vault!.Name}, using an owner-only file instead: {ex.Message}");
            return false;
        }
    }

    private SavedSteamLogin? Parse(string payload)
    {
        try
        {
            var login = JsonSerializer.Deserialize<SavedSteamLogin>(payload);
            return string.IsNullOrWhiteSpace(login?.AccountName) || string.IsNullOrWhiteSpace(login.RefreshToken) ? null : login;
        }
        catch (JsonException ex)
        {
            _log.Warn($"Ignoring an unreadable saved Steam login: {ex.Message}");
            return null;
        }
    }

    private void WriteOwnerOnlyFile(string payload)
    {
        // Created owner-only rather than tightened after writing, so the token is never briefly readable.
        // UnixCreateMode only applies to a new file, hence the delete.
        if (_fileSystem.File.Exists(FilePath)) _fileSystem.File.Delete(FilePath);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (_operatingSystemService.IsMacOS() || _operatingSystemService.IsLinux())
        {
#pragma warning disable CA1416
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
#pragma warning restore CA1416
        }

        using var stream = _fileSystem.FileStream.New(FilePath, options);
        using var writer = new StreamWriter(stream);
        writer.Write(payload);
    }
}
