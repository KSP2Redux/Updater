using System.Diagnostics;
using System.IO.Abstractions;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Ksp2Redux.Tools.Launcher.Services.Steam;

/// <summary>
/// An operating system secret store holding the serialized Steam login.
/// </summary>
public interface ISteamLoginVault
{
    /// <summary>A short name for log messages.</summary>
    string Name { get; }

    /// <returns>The stored payload, or null when nothing is stored.</returns>
    /// <exception cref="IOException">The store could not be read.</exception>
    string? Read();

    /// <summary>Replaces the stored payload.</summary>
    /// <exception cref="IOException">The store could not be written.</exception>
    void Write(string payload);

    /// <summary>Removes the stored payload, if any.</summary>
    void Delete();
}

/// <summary>
/// Encrypts the login with DPAPI, so only the current Windows user on this machine can decrypt it.
/// </summary>
[SupportedOSPlatform("windows")]
public class DpapiSteamLoginVault(IFileSystem fileSystem, string filePath) : ISteamLoginVault
{
    private static readonly byte[] ENTROPY = "KSP2Redux.SteamLogin"u8.ToArray();

    public string Name => "Windows data protection";

    public string? Read()
    {
        if (!fileSystem.File.Exists(filePath)) return null;

        try
        {
            var plain = ProtectedData.Unprotect(fileSystem.File.ReadAllBytes(filePath), ENTROPY, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            throw new IOException("The saved Steam login could not be decrypted.", ex);
        }
    }

    public void Write(string payload)
    {
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), ENTROPY, DataProtectionScope.CurrentUser);
        fileSystem.File.WriteAllBytes(filePath, encrypted);
    }

    public void Delete()
    {
        if (fileSystem.File.Exists(filePath)) fileSystem.File.Delete(filePath);
    }
}

/// <summary>
/// Keeps the login in the user's macOS login keychain.
/// </summary>
// Goes through Apple's `security` tool rather than the Keychain API: items are trusted to the tool that
// created them, and the ad-hoc signed launcher and CLI change signature every build, which would make
// macOS ask for the keychain password after each update. The secret travels on stdin, never in argv.
[SupportedOSPlatform("macos")]
public class KeychainSteamLoginVault(string service = KeychainSteamLoginVault.SERVICE) : ISteamLoginVault
{
    public const string SERVICE = "KSP2 Redux Steam login";
    private const string ACCOUNT = "steam-login";
    private const string SECURITY = "/usr/bin/security";
    private const int ITEM_NOT_FOUND = 44;
    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(15);

    public string Name => "the macOS keychain";

    public string? Read()
    {
        var (exitCode, output) = Run(null, "find-generic-password", "-s", service, "-a", ACCOUNT, "-w");
        if (exitCode == ITEM_NOT_FOUND) return null;
        if (exitCode != 0) throw new IOException($"The keychain could not be read (security exited with {exitCode}).");

        return Encoding.UTF8.GetString(Convert.FromBase64String(output.Trim()));
    }

    public void Write(string payload)
    {
        // Base64 keeps the secret free of quotes and spaces that the interactive command line would parse.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        Run($"add-generic-password -U -s \"{service}\" -a {ACCOUNT} -w {encoded}\n", "-i");

        // `security -i` exits 0 even when the command inside it fails, so the write is confirmed by reading back.
        if (Read() != payload) throw new IOException("The keychain did not keep the Steam login.");
    }

    public void Delete() => Run(null, "delete-generic-password", "-s", service, "-a", ACCOUNT);

    private static (int ExitCode, string Output) Run(string? input, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(SECURITY)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new IOException("The security tool could not be started.");
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TIMEOUT))
        {
            process.Kill();
            throw new IOException("The keychain did not answer.");
        }

        return (process.ExitCode, output.Result);
    }
}
