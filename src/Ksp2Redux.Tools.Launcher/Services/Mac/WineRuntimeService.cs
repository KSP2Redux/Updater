using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Mac;

/// <summary>Where a <see cref="WineRuntime"/> comes from.</summary>
public enum WineRuntimeKind
{
    /// <summary>The Wine and DXMT runtime shipped with the macOS launcher.</summary>
    Bundled,

    /// <summary>The player's own CrossOver install, using a dedicated bottle.</summary>
    CrossOver
}

/// <summary>
/// A way to run the Windows build of KSP2 on macOS.
/// </summary>
/// <param name="DisplayName">A short name to show the player.</param>
/// <param name="WineBinary">The <c>wine</c> executable to launch the game with.</param>
/// <param name="RuntimeRoot">The folder holding the runtime's files.</param>
/// <param name="PrefixPath">The Wine prefix or CrossOver bottle the game runs in.</param>
/// <param name="Environment">Extra environment variables from the runtime's <c>runtime.json</c>.</param>
public sealed record WineRuntime(
    WineRuntimeKind Kind,
    string DisplayName,
    string WineBinary,
    string RuntimeRoot,
    string PrefixPath,
    IReadOnlyDictionary<string, string>? Environment = null);

public interface IWineRuntimeService
{
    /// <summary>
    /// Finds the runtime to use: the bundled one when the launcher ships with it, otherwise CrossOver.
    /// </summary>
    /// <returns>The runtime, or null when neither is available.</returns>
    WineRuntime? Detect();

    /// <summary>
    /// Creates the Wine prefix or CrossOver bottle on first use. Safe to call before every launch.
    /// </summary>
    /// <exception cref="InvalidOperationException">Setting up the prefix or bottle failed.</exception>
    Task PrepareAsync(WineRuntime runtime, Action<string> log, CancellationToken cancellationToken);

    /// <summary>
    /// Builds the process that starts KSP2 inside the runtime.
    /// </summary>
    /// <param name="arguments">Game arguments, split on spaces. Quoting is not supported.</param>
    ProcessStartInfo CreateLaunchInfo(WineRuntime runtime, string exePath, string workingDirectory, string? arguments);

    /// <summary>
    /// Builds the process that force-stops everything running in the runtime's prefix, the game included.
    /// </summary>
    ProcessStartInfo CreateStopInfo(WineRuntime runtime);

    /// <summary>
    /// Says whether Rosetta 2 is installed. Both runtimes are Intel builds of Wine, so neither starts without it.
    /// </summary>
    bool IsRosettaInstalled();

    /// <summary>
    /// Works out where KSP2 keeps its saves inside the detected runtime's prefix.
    /// </summary>
    /// <returns>The folder path, which may not exist yet, or null when no runtime is available.</returns>
    string? GetGameDataFolder();
}

/// <summary>
/// Runs KSP2 through Wine and DXMT on macOS.
/// </summary>
// Bundled runtime layout, set by the packaging script: wine/ with DXMT in lib/wine, runtime.json, and
// prefix-files/ copied into drive_c/windows because DXMT needs winemetal.dll in system32.
public class WineRuntimeService(
    IFileSystem fileSystem,
    IEnvironmentProvider environmentProvider,
    ILauncherConfigService launcherConfigService,
    IProcessRunner processRunner,
    ILogService log) : IWineRuntimeService
{
    public const string RUNTIME_FOLDER = "wine-runtime";
    public const string RUNTIME_MANIFEST = "runtime.json";
    public const string PREFIX_FOLDER = "wine-prefix";
    public const string CROSSOVER_BOTTLE = "KSP2Redux";
    public const string CROSSOVER_APP = "/Applications/CrossOver.app";
    public const string LAUNCHER_APP = "KSP2 Redux.app";
    public const string ROSETTA_MARKER = "/Library/Apple/usr/share/rosetta/rosetta";

    /// <summary>Tells the player how to install Rosetta 2 when it is missing.</summary>
    public const string ROSETTA_MISSING_MESSAGE =
        "KSP2 runs through an Intel build of Wine, which needs Rosetta 2, and Rosetta is not installed on this Mac. " +
        "Install it by running this in Terminal, then try again:\n\nsoftwareupdate --install-rosetta --agree-to-license";
    private const string CROSSOVER_BIN = "Contents/SharedSupport/CrossOver/bin";
    private const string CROSSOVER_WINDOWS_USER = "crossover";
    private const string PUBLISHER_FOLDER = "Intercept Games";
    private const string GAME_FOLDER = "Kerbal Space Program 2";

    public WineRuntime? Detect()
    {
        foreach (var (root, isOwnBundle) in CandidateBundledRoots())
        {
            if (TryReadBundledRuntime(root) is not { } bundled) continue;
            if (isOwnBundle) RememberRuntimePath(root);
            return bundled;
        }

        var crossOverWine = fileSystem.Path.Combine(CROSSOVER_APP, CROSSOVER_BIN, "wine");
        if (fileSystem.File.Exists(crossOverWine))
        {
            var home = environmentProvider.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var bottle = fileSystem.Path.Combine(home, "Library", "Application Support", "CrossOver", "Bottles", CROSSOVER_BOTTLE);
            return new WineRuntime(WineRuntimeKind.CrossOver, "CrossOver", crossOverWine,
                fileSystem.Path.Combine(CROSSOVER_APP, CROSSOVER_BIN), bottle);
        }

        return null;
    }

    public async Task PrepareAsync(WineRuntime runtime, Action<string> logLine, CancellationToken cancellationToken)
    {
        if (fileSystem.Directory.Exists(fileSystem.Path.Combine(runtime.PrefixPath, "drive_c")))
        {
            if (runtime.Kind == WineRuntimeKind.Bundled) CopyPrefixFiles(runtime);
            return;
        }

        int exitCode;
        if (runtime.Kind == WineRuntimeKind.CrossOver)
        {
            logLine($"Creating the CrossOver bottle \"{CROSSOVER_BOTTLE}\"...");
            var startInfo = new ProcessStartInfo(fileSystem.Path.Combine(runtime.RuntimeRoot, "cxbottle"));
            foreach (var argument in new[]
                     {
                         "--bottle", CROSSOVER_BOTTLE, "--create", "--template", "win10_64",
                         "--description", "KSP2 Redux",
                         // Same Direct3D 11 to Metal backend as the bundled runtime.
                         "--param", "EnvironmentVariables:CX_GRAPHICS_BACKEND=dxmt"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            exitCode = await processRunner.RunAsync(startInfo, logLine, cancellationToken);
        }
        else
        {
            logLine("Setting up the Windows environment for KSP2. This only happens once...");
            fileSystem.Directory.CreateDirectory(runtime.PrefixPath);
            var startInfo = new ProcessStartInfo(runtime.WineBinary);
            startInfo.ArgumentList.Add("wineboot");
            startInfo.ArgumentList.Add("--init");
            ApplyBundledEnvironment(startInfo, runtime);
            exitCode = await processRunner.RunAsync(startInfo, logLine, cancellationToken);
            await processRunner.RunAsync(WineServerWait(runtime), logLine, cancellationToken);
            if (exitCode == 0) CopyPrefixFiles(runtime);
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Setting up {runtime.DisplayName} failed with exit code {exitCode}.");
        }

        log.Info($"Prepared {runtime.Kind} runtime at {runtime.PrefixPath}.");
    }

    public ProcessStartInfo CreateLaunchInfo(WineRuntime runtime, string exePath, string workingDirectory, string? arguments)
    {
        var startInfo = new ProcessStartInfo(runtime.WineBinary)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };

        if (runtime.Kind == WineRuntimeKind.CrossOver)
        {
            startInfo.ArgumentList.Add("--bottle");
            startInfo.ArgumentList.Add(CROSSOVER_BOTTLE);
            startInfo.ArgumentList.Add("--workdir");
            startInfo.ArgumentList.Add(workingDirectory);
            startInfo.ArgumentList.Add("--");
        }
        else
        {
            ApplyBundledEnvironment(startInfo, runtime);
        }

        startInfo.ArgumentList.Add(exePath);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    public bool IsRosettaInstalled() => fileSystem.File.Exists(ROSETTA_MARKER);

    public ProcessStartInfo CreateStopInfo(WineRuntime runtime)
    {
        // CrossOver hides wineserver behind its wrapper, so its bottle is stopped through wineboot.
        if (runtime.Kind == WineRuntimeKind.CrossOver)
        {
            var crossOver = new ProcessStartInfo(runtime.WineBinary) { UseShellExecute = false };
            foreach (var argument in new[] { "--bottle", CROSSOVER_BOTTLE, "--", "wineboot", "--kill" })
            {
                crossOver.ArgumentList.Add(argument);
            }

            return crossOver;
        }

        var wineServer = fileSystem.Path.Combine(fileSystem.Path.GetDirectoryName(runtime.WineBinary)!, "wineserver");
        var startInfo = new ProcessStartInfo(wineServer) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--kill");
        ApplyBundledEnvironment(startInfo, runtime);
        return startInfo;
    }

    public string? GetGameDataFolder()
    {
        if (Detect() is not { } runtime) return null;

        return fileSystem.Path.Combine(runtime.PrefixPath, "drive_c", "users", FindWindowsUser(runtime),
            "AppData", "LocalLow", PUBLISHER_FOLDER, GAME_FOLDER);
    }

    // Wine names the Windows user after the macOS account, but CrossOver-derived builds always use "crossover".
    private string FindWindowsUser(WineRuntime runtime)
    {
        var users = fileSystem.Path.Combine(runtime.PrefixPath, "drive_c", "users");
        if (fileSystem.Directory.Exists(users))
        {
            var user = fileSystem.Directory.EnumerateDirectories(users)
                .Select(fileSystem.Path.GetFileName)
                .FirstOrDefault(name => !string.IsNullOrEmpty(name) && !string.Equals(name, "Public", StringComparison.OrdinalIgnoreCase));
            if (user is not null) return user;
        }

        return runtime.Kind == WineRuntimeKind.CrossOver ? CROSSOVER_WINDOWS_USER : environmentProvider.UserName;
    }

    // wineboot returns before the prefix has finished writing its registry.
    private ProcessStartInfo WineServerWait(WineRuntime runtime)
    {
        var wineServer = fileSystem.Path.Combine(fileSystem.Path.GetDirectoryName(runtime.WineBinary)!, "wineserver");
        var startInfo = new ProcessStartInfo(wineServer);
        startInfo.ArgumentList.Add("--wait");
        ApplyBundledEnvironment(startInfo, runtime);
        return startInfo;
    }

    private IEnumerable<(string Root, bool IsOwnBundle)> CandidateBundledRoots()
    {
        // Inside the .app the executable sits in Contents/MacOS, and resources in Contents/Resources.
        var processPath = environmentProvider.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && fileSystem.Path.GetDirectoryName(processPath) is { } exeDir)
        {
            yield return (fileSystem.Path.GetFullPath(fileSystem.Path.Combine(exeDir, "..", "Resources", RUNTIME_FOLDER)), true);
            yield return (fileSystem.Path.Combine(exeDir, RUNTIME_FOLDER), true);
        }

        yield return (fileSystem.Path.Combine(launcherConfigService.GetLocalStorageDirectory(), RUNTIME_FOLDER), false);

        if (!string.IsNullOrWhiteSpace(launcherConfigService.Config.WineRuntimePath))
        {
            yield return (launcherConfigService.Config.WineRuntimePath, false);
        }

        var home = environmentProvider.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var applications in new[] { "/Applications", fileSystem.Path.Combine(home, "Applications") })
        {
            yield return (fileSystem.Path.Combine(applications, LAUNCHER_APP, "Contents", "Resources", RUNTIME_FOLDER), false);
        }
    }

    private void RememberRuntimePath(string root)
    {
        if (string.Equals(launcherConfigService.Config.WineRuntimePath, root, StringComparison.Ordinal)) return;
        launcherConfigService.Config.WineRuntimePath = root;
        launcherConfigService.Save();
    }

    private WineRuntime? TryReadBundledRuntime(string root)
    {
        var manifestPath = fileSystem.Path.Combine(root, RUNTIME_MANIFEST);
        if (!fileSystem.File.Exists(manifestPath)) return null;

        try
        {
            var manifest = JsonSerializer.Deserialize<RuntimeManifest>(fileSystem.File.ReadAllText(manifestPath));
            var wineBinary = fileSystem.Path.Combine(root, manifest?.WineBinary ?? "wine/bin/wine");
            if (!fileSystem.File.Exists(wineBinary))
            {
                log.Warn($"Bundled runtime at {root} has no wine binary at {wineBinary}.");
                return null;
            }

            var prefix = fileSystem.Path.Combine(launcherConfigService.GetLocalStorageDirectory(), PREFIX_FOLDER);
            var name = string.IsNullOrWhiteSpace(manifest?.Wine) ? "Bundled runtime" : manifest.Wine;
            return new WineRuntime(WineRuntimeKind.Bundled, name, wineBinary, root, prefix, manifest?.PrefixEnvironment);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"Couldn't read the bundled runtime manifest at {manifestPath}: {ex.Message}");
            return null;
        }
    }

    private static void ApplyBundledEnvironment(ProcessStartInfo startInfo, WineRuntime runtime)
    {
        startInfo.Environment["WINEDEBUG"] = "-all";
        startInfo.Environment["WINEMSYNC"] = "1";

        foreach (var (key, value) in runtime.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        startInfo.Environment["WINEPREFIX"] = runtime.PrefixPath;
    }

    private void CopyPrefixFiles(WineRuntime runtime)
    {
        var source = fileSystem.Path.Combine(runtime.RuntimeRoot, "prefix-files");
        if (!fileSystem.Directory.Exists(source)) return;

        var target = fileSystem.Path.Combine(runtime.PrefixPath, "drive_c", "windows");
        foreach (var file in fileSystem.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = fileSystem.Path.Combine(target, fileSystem.Path.GetRelativePath(source, file));
            fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(destination)!);
            fileSystem.File.Copy(file, destination, overwrite: true);
        }
    }

    private sealed class RuntimeManifest
    {
        [JsonPropertyName("wine")] public string? Wine { get; set; }
        [JsonPropertyName("dxmt")] public string? Dxmt { get; set; }
        [JsonPropertyName("wineBinary")] public string? WineBinary { get; set; }
        [JsonPropertyName("prefixEnv")] public Dictionary<string, string>? PrefixEnvironment { get; set; }
    }
}
