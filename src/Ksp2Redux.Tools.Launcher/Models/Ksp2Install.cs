using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Mono.Cecil;

namespace Ksp2Redux.Tools.Launcher.Models;

public class Ksp2Install
{
    private readonly IFileSystem _fileSystem;
    private readonly IModuleDefinitionService _moduleDefinitionService;

    /// <summary>
    /// If the path pointed to represents a valid KSP2 installation or not.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// The distribution of the current install
    /// </summary>
    public Distribution Distribution { get; }

    /// <summary>
    /// Game version deteced from reflection.
    /// </summary>
    public GameVersion? GameVersion { get; private set; }

    /// <summary>
    /// Gets the reconciled full-source generation identity, or null for stock and legacy installations.
    /// </summary>
    public FullSourceReleaseIdentity? ManagedRelease { get; }

    /// <summary>
    /// Compare a selected version and recipe contract with the published installation generation.
    /// </summary>
    /// <param name="version">The selected feed version.</param>
    /// <param name="plan">The selected plan, or null for an installed or legacy version.</param>
    /// <returns>True if the selected release is installed, false otherwise.</returns>
    public bool IsInstalledVersion(GameVersion version, InstallPlan? plan = null) =>
        plan?.IsFullSource == true
            ? ManagedRelease is { } release && release.ReleaseId == plan.FullSourceReleaseId &&
                release.Channel == plan.FullSourceChannel && release.Version == plan.FullSourceVersion &&
                release.ManifestSha256.Equals(plan.FullSourceManifestSha256, StringComparison.OrdinalIgnoreCase)
            : version.Equals(GameVersion);

    public Exception? VersionDetectionException { get; private set; }

    public string InstallDir { get; }

    public string ExePath { get; }

    public const string KSP2_EXE_NAME = "KSP2_x64.exe";

    private static string GetManagedFolder(IFileSystem fileSystem, string installDir)
        => fileSystem.Path.Combine(installDir, "KSP2_x64_Data", "Managed");

    private const string MainAssemblyFileName = "Assembly-CSharp.dll";
    private const string VersionTypeName = "VersionID";

    // Only Redux builds declare it, so it tells Redux game code from stock without trusting the folder layout.
    private const string ReduxChannelField = "CHANNEL_NAME";

    // Exists in Steam based installations, but doesn't exist in portable version.
    private const string SteamworksText = "KSP2_x64_Data/Plugins/Steamworks.NET.txt";
    private const string EpicGamesMarker = ".egstore";
    private const string PrepatchMarker = "prepatched.nodelete";

    // Revert-to-stock keeps these folders under Redux: the config, and the bundle conversion journal with its
    // converted copies. A Redux folder holding only them is a stock install.
    private static readonly HashSet<string> KeptReduxFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Config",
        "BundleConversion",
    };

    /// <summary>
    /// Inspect an installation, using a reconciled launcher release identity for managed full-source builds.
    /// </summary>
    /// <param name="fileSystem">The filesystem abstraction.</param>
    /// <param name="moduleDefinitionService">The legacy assembly metadata reader.</param>
    /// <param name="exePath">The configured game executable.</param>
    /// <param name="managedRelease">The reconciled catalog release identity, or null for legacy and stock installs.</param>
    public Ksp2Install(IFileSystem fileSystem, IModuleDefinitionService moduleDefinitionService, string exePath,
        FullSourceReleaseIdentity? managedRelease = null)
    {
        _fileSystem = fileSystem;
        _moduleDefinitionService = moduleDefinitionService;
        ExePath = exePath;
        ManagedRelease = managedRelease;

        IsValid = _fileSystem.File.Exists(exePath) && _fileSystem.Path.GetFileName(exePath) == KSP2_EXE_NAME;
        if (IsValid || managedRelease is not null)
        {
            InstallDir = _fileSystem.Path.GetDirectoryName(exePath)!;
            var isSteam = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, SteamworksText));
            var isEpic = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, EpicGamesMarker));
            var reduxDir = _fileSystem.Path.Combine(InstallDir, "Redux");
            var isRedux = _fileSystem.Directory.Exists(reduxDir)
                && _fileSystem.Directory.EnumerateFileSystemEntries(reduxDir)
                    .Any(p => !KeptReduxFolders.Contains(_fileSystem.Path.GetFileName(p)));
            var isPrepatch = _fileSystem.Path.Exists(_fileSystem.Path.Combine(InstallDir, PrepatchMarker));

            bool? gameCodeIsRedux = managedRelease is not null ? true : TryDetectGameVersionFromMainAssembly(InstallDir);
            if (managedRelease is not null)
            {
                string[] components = managedRelease.Version.Split('.');
                GameVersion = new GameVersion
                {
                    VersionNumber = Version.Parse(string.Join('.', components[..4])), BuildNumber = components[4],
                    Channel = managedRelease.Channel
                };
            }

            // Verifying the game files through the store puts the stock game code back but leaves everything Redux
            // added, so the Redux folder alone is not proof. When the game code was read, it decides.
            Distribution = gameCodeIsRedux ?? isRedux ? Distribution.Redux :
                isPrepatch ? Distribution.Prepatched :
                isSteam ? Distribution.Steam :
                isEpic ? Distribution.Epic :
                Distribution.Portable;
        }
        else
        {
            InstallDir = "";
        }
    }

    // Returns whether the game code is a Redux build, or null when no version could be read.
    private bool? TryDetectGameVersionFromMainAssembly(string installDir)
    {
        try
        {
            using var versionModule = ReadModuleDeclaringVersionId(GetManagedFolder(_fileSystem, installDir));
            var versionType = versionModule.Types.First(t => t.Name == VersionTypeName);
            var isRedux = versionType.Fields.Any(f => f.Name == ReduxChannelField);

            GameVersion = GameVersion.FromVersionIDType(versionType, isRedux);
            return isRedux;
        }
        catch (Exception e)
        {
            GameVersion = null;
            VersionDetectionException = e;
            return null;
        }
    }

    // Stock KSP2 and Redux builds up to the repository consolidation keep VersionID in Assembly-CSharp. Later
    // builds compile the game into its own assembly and ship an Assembly-CSharp made only of type forwarders,
    // so the forwarder names the assembly to read instead.
    private ModuleDefinition ReadModuleDeclaringVersionId(string managedFolder)
    {
        var mainModule = _moduleDefinitionService.ReadModule(_fileSystem.Path.Combine(managedFolder, MainAssemblyFileName));
        if (mainModule.Types.Any(t => t.Name == VersionTypeName))
        {
            return mainModule;
        }

        var forwarder = mainModule.ExportedTypes.FirstOrDefault(t => t.Name == VersionTypeName && t.IsForwarder);
        if (forwarder?.Scope is not AssemblyNameReference targetAssembly)
        {
            mainModule.Dispose();
            throw new InvalidOperationException($"{MainAssemblyFileName} neither declares nor forwards {VersionTypeName}.");
        }

        mainModule.Dispose();
        return _moduleDefinitionService.ReadModule(_fileSystem.Path.Combine(managedFolder, $"{targetAssembly.Name}.dll"));
    }

    public override string ToString()
    {
        return
            $"Ksp2Install: IsValid:{IsValid} Distribution:{Distribution} GameVersion:{GameVersion} Dir:\"{InstallDir}\"";
    }
}
