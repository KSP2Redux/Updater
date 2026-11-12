using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using SteamKit2;

namespace Ksp2Redux.Tools.Launcher.Services.Linux;

/// <summary>
/// Prepares and launches the Linux launcher's managed Proton environment.
/// </summary>
public interface IProtonRuntimeService
{
    /// <summary>
    /// Installs verified umu code, then lets umu download and verify Proton and its runtime and prepare the prefix.
    /// </summary>
    /// <param name="log">Receives download progress and runtime output.</param>
    /// <param name="cancellationToken">Cancels downloads and runtime preparation.</param>
    /// <returns>A task that completes once the managed prefix is ready.</returns>
    Task PrepareAsync(Action<LogMessage> log, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a game launch through the managed umu and Proton environment.
    /// </summary>
    /// <param name="exePath">The Windows game executable.</param>
    /// <param name="workingDirectory">The game installation directory.</param>
    /// <param name="arguments">Game arguments, with the same quoting rules as direct launching.</param>
    /// <returns>The process information for the launch.</returns>
    ProcessStartInfo CreateLaunchInfo(string exePath, string workingDirectory, string? arguments);

    /// <summary>
    /// Gets the saves, settings and game log directory inside the managed prefix.
    /// </summary>
    /// <returns>The Windows persistent-data directory, which may not exist before the first launch.</returns>
    string GetGameDataFolder();
}

/// <summary>
/// Owns the launcher-managed umu bootstrap, shared Proton downloads and dedicated Wine prefix.
/// </summary>
/// <remarks>
/// umu provides GE-Proton's supported non-Steam execution path and verifies the Proton and Steam runtime downloads.
/// Its small Python zipapp is pinned here and verified before it is published or executed.
/// </remarks>
public sealed class ProtonRuntimeService : IProtonRuntimeService, IDisposable
{
    private const string UMU_VERSION = "1.4.4";
    private const string BOOTSTRAP_URL = "https://github.com/Open-Wine-Components/umu-launcher/releases/download/1.4.4/umu-launcher-1.4.4-zipapp.tar";
    private const string ARCHIVE_SHA256 = "eb590691841f7fad3fc3ad8fd5db4ccb87849fe7948e62b28ece7a4ee48cc851";
    private const string BOOTSTRAP_SHA256 = "d0005a58602041229cc467dab03dc0c0b9e8cce09a8145b16b7683244cf17804";
    private const int MAX_BOOTSTRAP_BYTES = 8 * 1024 * 1024;

    private readonly IFileSystem _fileSystem;
    private readonly IEnvironmentProvider _environmentProvider;
    private readonly IProcessRunner _processRunner;
    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _preparation = new(1, 1);
    private readonly string _runtimeDirectory;
    private readonly string _bootstrapPath;
    private readonly string _prefixPath;
    private string _protonPath = "GE-Proton";

    /// <summary>
    /// Creates the managed runtime service.
    /// </summary>
    /// <param name="fileSystem">The filesystem used for runtime storage.</param>
    /// <param name="environmentProvider">The host environment provider.</param>
    /// <param name="launcherConfigService">The launcher storage owner.</param>
    /// <param name="processRunner">Runs Python checks and cancellable umu preparation.</param>
    /// <param name="log">The launcher log.</param>
    /// <param name="httpClient">An optional download client. The caller retains its ownership.</param>
    public ProtonRuntimeService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider,
        ILauncherConfigService launcherConfigService, IProcessRunner processRunner, ILogService log,
        HttpClient? httpClient = null)
    {
        _fileSystem = fileSystem;
        _environmentProvider = environmentProvider;
        _processRunner = processRunner;
        _log = log;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        string storage = launcherConfigService.GetLocalStorageDirectory();
        _runtimeDirectory = fileSystem.Path.Combine(storage, "proton-runtime");
        _bootstrapPath = fileSystem.Path.Combine(_runtimeDirectory, "bootstrap", UMU_VERSION, "umu-run.pyz");
        _prefixPath = fileSystem.Path.Combine(storage, "proton-prefix");
    }

    /// <inheritdoc />
    public async Task PrepareAsync(Action<LogMessage> log, CancellationToken cancellationToken)
    {
        await _preparation.WaitAsync(cancellationToken);
        try
        {
            await CheckPythonAsync(log, cancellationToken);
            await EnsureBootstrapAsync(log, cancellationToken);
            await PrepareRuntimeAsync(log, cancellationToken);
        }
        finally
        {
            _preparation.Release();
        }
    }

    /// <inheritdoc />
    public ProcessStartInfo CreateLaunchInfo(string exePath, string workingDirectory, string? arguments)
        => CreateRuntimeProcess(workingDirectory, exePath, arguments);

    /// <inheritdoc />
    public string GetGameDataFolder() => _fileSystem.Path.Combine(_prefixPath, "drive_c", "users", "steamuser",
        "AppData", "LocalLow", "Intercept Games", "Kerbal Space Program 2");

    private bool HasPrefix() => _fileSystem.File.Exists(_fileSystem.Path.Combine(_prefixPath, "system.reg"));

    internal async Task PrepareRuntimeAsync(Action<LogMessage> log, CancellationToken cancellationToken)
    {
        _fileSystem.Directory.CreateDirectory(_prefixPath);
        log(LocalizationService.Default.Message("Proton.Preparing"));
        string? installed = FindInstalledProton();
        _protonPath = installed ?? "GE-Proton";
        log(LocalizationService.Default.Message(installed is null ? "Proton.DownloadingRuntime" : "Proton.ReusingRuntime", installed));

        // A Windows no-op also checks that the prepared prefix starts. Some Proton builds return a failure for an empty executable.
        ProcessStartInfo prepare = CreateRuntimeProcess(_runtimeDirectory, "cmd.exe", "/c exit 0");
        int exitCode = await _processRunner.RunAsync(prepare, log, cancellationToken);
        if (installed is not null && (exitCode != 0 || !HasPrefix()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            log(LocalizationService.Default.Message("Proton.InstalledFailed", installed));
            _log.Warn($"Installed Proton at {installed} could not prepare the prefix (exit {exitCode}). Trying managed GE-Proton.");
            _protonPath = "GE-Proton";
            exitCode = await _processRunner.RunAsync(CreateRuntimeProcess(_runtimeDirectory, "cmd.exe", "/c exit 0"), log, cancellationToken);
        }
        if (exitCode != 0)
            throw Failure("Proton.PrepareFailed", exitCode);
        if (!HasPrefix())
            throw Failure("Proton.PrefixMissing");

        log(LocalizationService.Default.Message("Proton.Ready"));
        _log.Info($"Managed umu {UMU_VERSION} prepared Proton at {_prefixPath}.");
    }

    internal string? FindInstalledProton()
    {
        string? selected = null;
        Version? selectedVersion = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in Ksp2DetectorService.EnumerateLinuxSteamRoots(_fileSystem, _environmentProvider))
        {
            if (!_fileSystem.Directory.Exists(root))
                continue;
            foreach (string library in Ksp2DetectorService.ReadLibraryFolders(_fileSystem, _log, root))
            {
                foreach (string container in new[] { _fileSystem.Path.Combine(library, "compatibilitytools.d"), _fileSystem.Path.Combine(library, "steamapps", "common") })
                {
                    if (!visited.Add(container) || !_fileSystem.Directory.Exists(container))
                        continue;
                    try
                    {
                        foreach (string candidate in _fileSystem.Directory.EnumerateDirectories(container))
                        {
                            Version? version;
                            try
                            {
                                version = ReadSupportedProtonVersion(candidate);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                _log.Warn($"Could not inspect Proton at {candidate}: {exception.Message}");
                                continue;
                            }
                            if (version is null || (selectedVersion is not null && version <= selectedVersion))
                                continue;
                            selected = candidate;
                            selectedVersion = version;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _log.Warn($"Could not inspect Proton installations in {container}: {exception.Message}");
                    }
                }
            }
        }
        return selected;
    }

    private Version? ReadSupportedProtonVersion(string directory)
    {
        string name = _fileSystem.Path.GetFileName(directory);
        Match version = Regex.Match(name, @"^(?:GE-Proton|UMU-Proton|Proton )(?<major>\d+)[.-](?<minor>\d+)(?:[.-](?<patch>\d+))?$", RegexOptions.CultureInvariant);
        if (!version.Success || !int.TryParse(version.Groups["major"].Value, out int major) || major < 8 ||
            !int.TryParse(version.Groups["minor"].Value, out int minor))
            return null;
        int patch = 0;
        if (version.Groups["patch"].Success && !int.TryParse(version.Groups["patch"].Value, out patch))
            return null;

        string manifestPath = _fileSystem.Path.Combine(directory, "toolmanifest.vdf");
        foreach (string required in new[] { "proton", "version", "toolmanifest.vdf", "files/bin/wine", "files/bin/wineserver" })
        {
            if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(directory, required)) ||
                _fileSystem.FileInfo.New(_fileSystem.Path.Combine(directory, required)).Length == 0)
                return null;
        }
        try
        {
            KeyValue? manifest = KeyValue.LoadFromString(_fileSystem.File.ReadAllText(manifestPath));
            // umu supports these x86-64 Steam containers. Experimental tools and host-built Wine are not selected automatically.
            if (manifest is null || manifest["compatmanager_layer_name"].Value != "proton" ||
                manifest["commandline"].Value != "/proton %verb%" ||
                manifest["require_tool_appid"].Value is not ("1628350" or "4183110"))
                return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read the installed Proton manifest at {manifestPath}: {exception.Message}");
            return null;
        }
        return new Version(major, minor, patch);
    }

    private async Task CheckPythonAsync(Action<LogMessage> log, CancellationToken cancellationToken)
    {
        var check = new ProcessStartInfo("python3") { UseShellExecute = false };
        check.ArgumentList.Add("-I");
        check.ArgumentList.Add("-c");
        check.ArgumentList.Add("import platform,sys; sys.exit(0 if sys.platform == 'linux' and platform.machine().lower() in ('x86_64','amd64') and sys.version_info >= (3,10) else 1)");
        try
        {
            if (await _processRunner.RunAsync(check, log, cancellationToken) != 0)
                throw Failure("Proton.PythonRequired");
        }
        catch (Win32Exception exception)
        {
            throw LocalizationService.Default.Message("Proton.PythonRequired")
                .ToException(message => new InvalidOperationException(message, exception));
        }
    }

    private async Task EnsureBootstrapAsync(Action<LogMessage> log, CancellationToken cancellationToken)
    {
        if (_fileSystem.File.Exists(_bootstrapPath))
        {
            await using Stream existing = _fileSystem.File.OpenRead(_bootstrapPath);
            if (existing.Length <= MAX_BOOTSTRAP_BYTES &&
                Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken))
                    .Equals(BOOTSTRAP_SHA256, StringComparison.OrdinalIgnoreCase))
                return;

            _log.Warn("The managed umu bootstrap is incomplete or modified. Downloading the verified release again.");
        }

        log(LocalizationService.Default.Message("Proton.Downloading", UMU_VERSION));
        using var request = new HttpRequestMessage(HttpMethod.Get, BOOTSTRAP_URL);
        request.Headers.UserAgent.ParseAdd("KSP2Redux-Launcher");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MAX_BOOTSTRAP_BYTES)
            throw Failure("Proton.BootstrapTooLarge");

        await using Stream download = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = await download.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (archive.Length + count > MAX_BOOTSTRAP_BYTES)
                throw Failure("Proton.BootstrapTooLarge");

            await archive.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        byte[] bootstrap = ReadBootstrap(archive.ToArray(), ARCHIVE_SHA256, BOOTSTRAP_SHA256);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = _fileSystem.Path.GetDirectoryName(_bootstrapPath)!;
        _fileSystem.Directory.CreateDirectory(directory);
        string staging = _fileSystem.Path.Combine(directory, $".umu-{Guid.NewGuid():N}.tmp");
        try
        {
            await _fileSystem.File.WriteAllBytesAsync(staging, bootstrap, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.File.Move(staging, _bootstrapPath, overwrite: true);
        }
        finally
        {
            _fileSystem.File.Delete(staging);
        }
    }

    internal static byte[] ReadBootstrap(byte[] archive, string expectedArchiveHash, string expectedBootstrapHash)
    {
        if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(expectedArchiveHash, StringComparison.OrdinalIgnoreCase))
            throw Failure("Proton.ChecksumFailed");

        using var stream = new MemoryStream(archive, writable: false);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            // Only copy the known regular file. Archive paths and symlinks are never extracted onto the filesystem.
            if (entry.Name != "umu/umu-run")
                continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null || entry.Length > MAX_BOOTSTRAP_BYTES)
                throw Failure("Proton.BootstrapInvalid");

            using var binary = new MemoryStream();
            entry.DataStream.CopyTo(binary);
            byte[] contents = binary.ToArray();
            if (!Convert.ToHexString(SHA256.HashData(contents)).Equals(expectedBootstrapHash, StringComparison.OrdinalIgnoreCase))
                throw Failure("Proton.ChecksumFailed");
            return contents;
        }

        throw Failure("Proton.BootstrapInvalid");
    }

    private ProcessStartInfo CreateRuntimeProcess(string workingDirectory, string exePath, string? arguments)
    {
        var startInfo = new ProcessStartInfo("python3")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            // Let Process parse user arguments exactly as it does for a direct launch, without running a shell.
            Arguments = $"-I {QuoteArgument(_bootstrapPath)} {QuoteArgument(exePath)}" +
                        (string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments}")
        };
        startInfo.Environment["WINEPREFIX"] = _prefixPath;
        startInfo.Environment["UMU_FOLDERS_PATH"] = _runtimeDirectory;
        startInfo.Environment["PROTONPATH"] = _protonPath;
        startInfo.Environment["GAMEID"] = "0";
        startInfo.Environment["STORE"] = "none";
        startInfo.Environment["PROTON_VERB"] = "waitforexitandrun";
        startInfo.Environment["UMU_RUNTIME_UPDATE"] = "1";
        startInfo.Environment["UMU_LOG"] = "info";
        foreach (string inherited in new[] { "UMU_NO_RUNTIME", "UMU_NO_PROTON", "UMU_ZENITY", "RUNTIMEPATH", "UMU_CONTAINER_NSENTER", "STEAM_COMPAT_DATA_PATH", "SteamAppId", "SteamGameId" })
        {
            startInfo.Environment.Remove(inherited);
        }
        startInfo.Environment["STEAM_COMPAT_INSTALL_PATH"] = workingDirectory;
        return startInfo;
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }

    private static InvalidOperationException Failure(string key, params object?[] arguments)
        => LocalizationService.Default.Message(key, arguments).ToException(message => new InvalidOperationException(message));

    /// <inheritdoc />
    public void Dispose()
    {
        _preparation.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
