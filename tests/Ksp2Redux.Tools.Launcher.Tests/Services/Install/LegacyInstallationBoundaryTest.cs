using System.Buffers.Binary;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Common.Wrappers;
using Ksp2Redux.Tools.Launcher.Tests.Cli;
using Moq;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Exercises production legacy entry points against protected evidence in disposable installations.
/// </summary>
public class LegacyInstallationBoundaryTest
{
    private const string INSTALL_ROOT = @"C:\Games\Boundary";
    private const string MARKER = ".redux-transaction.active";

    private static MockFileSystem CreateFileSystem()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64_Data", "Managed"));
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetTempPath());
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe"), "synthetic game");
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, "unknown.txt"), "unowned bytes");
        return fileSystem;
    }

    private static InstallationStateService State(MockFileSystem fileSystem) =>
        new(fileSystem, new ZipFileService(fileSystem));

    private static void WriteArchive(MockFileSystem fileSystem, params string[] names)
    {
        using Stream input = fileSystem.File.Create(fileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip"));
        using var archive = new ZipArchive(input, ZipArchiveMode.Create);
        foreach (string name in names)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(name == StockBaselineManifest.MANIFEST_PATH ? "not authenticated JSON" : "stock bytes");
        }
    }

    private static Dictionary<string, byte[]> Snapshot(MockFileSystem fileSystem) =>
        fileSystem.Directory.EnumerateFiles(INSTALL_ROOT, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, fileSystem.File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertUnchanged(MockFileSystem fileSystem, Dictionary<string, byte[]> before)
    {
        Assert.That(Snapshot(fileSystem), Is.EqualTo(before), "All installation and evidence bytes must survive rejection.");
        Assert.That(fileSystem.Directory.EnumerateFiles(fileSystem.Path.GetTempPath()), Is.Empty);
    }

    /// <summary>
    /// Full archive metadata prevents cache replacement and destructive restoration.
    /// </summary>
    /// <param name="restore">True for restoration, false for cache creation.</param>
    /// <param name="repatch">True for restoration that preserves the legacy snapshot.</param>
    /// <param name="manifestName">The archive marker name, including ambiguous aliases.</param>
    [TestCase(false, false, StockBaselineManifest.MANIFEST_PATH)]
    [TestCase(true, false, StockBaselineManifest.MANIFEST_PATH)]
    [TestCase(true, true, StockBaselineManifest.MANIFEST_PATH)]
    [TestCase(false, false, ".REDUX-STOCK\\MANIFEST.JSON")]
    [TestCase(true, false, ".redux-stock/manifest.json/")]
    public void FullArchiveSurvivesLegacyCacheOperations(bool restore, bool repatch, string manifestName)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, manifestName, "KSP2_x64_Data/StreamingAssets/stock.bundle");
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(INSTALL_ROOT, "Redux", "Config"));
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, "Redux", "Config", "settings.json"), "config");
        var before = Snapshot(fileSystem);
        var service = new CacheService(fileSystem, new ZipFileService(fileSystem), State(fileSystem));

        var error = Assert.Throws<IOException>(() =>
        {
            if (restore)
                service.RecursivelyRestoreCache(INSTALL_ROOT, repatch);
            else
                service.RecursivelyCreateCache(INSTALL_ROOT);
        });

        Assert.That(error!.Message, Does.Contain("claims a full stock backup"));
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Even empty and foreign transaction markers block destructive cache operations.
    /// </summary>
    /// <param name="marker">The uninterpreted marker bytes.</param>
    /// <param name="restore">True for restoration, false for cache creation.</param>
    [TestCase("", false)]
    [TestCase("a", true)]
    [TestCase("{broken", false)]
    [TestCase("completed", true)]
    public void TransactionMarkerBytesNeverAuthorizeLegacyMutation(string marker, bool restore)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, "original.txt");
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), marker);
        var before = Snapshot(fileSystem);
        var service = new CacheService(fileSystem, new ZipFileService(fileSystem), State(fileSystem));

        Assert.Throws<IOException>(() =>
        {
            if (restore)
                service.RecursivelyRestoreCache(INSTALL_ROOT);
            else
                service.RecursivelyCreateCache(INSTALL_ROOT);
        });

        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// A reserved marker directory protects the installation as well as a regular file.
    /// </summary>
    [Test]
    public void TransactionMarkerDirectoryPreventsLegacyDeletion()
    {
        var fileSystem = CreateFileSystem();
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(INSTALL_ROOT, MARKER));
        var before = Snapshot(fileSystem);

        Assert.Throws<IOException>(() => new CacheService(fileSystem, new ZipFileService(fileSystem), State(fileSystem))
            .RecursivelyCreateCache(INSTALL_ROOT));

        Assert.That(fileSystem.Directory.Exists(fileSystem.Path.Combine(INSTALL_ROOT, MARKER)), Is.True);
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Damaged and ambiguous archives are preserved before any purge or replacement.
    /// </summary>
    /// <param name="kind">The archive failure shape.</param>
    /// <param name="restore">True for restoration, false for creation.</param>
    [TestCase("truncated", true)]
    [TestCase("truncated", false)]
    [TestCase("duplicates", true)]
    [TestCase("false-count", false)]
    [TestCase("budget", true)]
    [TestCase("ancestor", true)]
    [TestCase("local-header", false)]
    [TestCase("deep-name", true)]
    [TestCase("long-name", false)]
    public void UninspectableArchiveSurvivesLegacyOperations(string kind, bool restore)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, "original.txt", kind switch
        {
            "duplicates" => "ORIGINAL.TXT",
            "ancestor" => "original.txt/child",
            "deep-name" => string.Concat(Enumerable.Repeat("a/", 129)) + "end",
            "long-name" => new string('a', 4097),
            _ => "other.txt"
        });
        string path = fileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip");
        byte[] bytes = fileSystem.File.ReadAllBytes(path);
        if (kind == "truncated")
            bytes = bytes[..^12];
        if (kind == "false-count")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 1);
        }
        if (kind == "budget")
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 12), 32 * 1024 * 1024);
        if (kind == "local-header")
            bytes[0] = 0;
        fileSystem.File.WriteAllBytes(path, bytes);
        var before = Snapshot(fileSystem);
        var service = new CacheService(fileSystem, new ZipFileService(fileSystem), State(fileSystem));

        Assert.Throws<IOException>(() =>
        {
            if (restore)
                service.RecursivelyRestoreCache(INSTALL_ROOT);
            else
                service.RecursivelyCreateCache(INSTALL_ROOT);
        });

        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// An archive held without sharing cannot be removed to make inspection succeed.
    /// </summary>
    /// <param name="restore">True for restoration, false for creation.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void LockedArchiveIsPreserved(bool restore)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        var before = Snapshot(fileSystem);
        using (fileSystem.File.Open(fileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var service = new CacheService(fileSystem, new ZipFileService(fileSystem), State(fileSystem));
            var error = Assert.Throws<IOException>(() =>
            {
                if (restore)
                    service.RecursivelyRestoreCache(INSTALL_ROOT);
                else
                    service.RecursivelyCreateCache(INSTALL_ROOT);
            });
            Assert.That(error!.Message, Does.Contain("could not be safely inspected"));
        }
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Plan rejection precedes file checks, downloads, journal reversal and prepatch deletion.
    /// </summary>
    /// <param name="pending">True for pending transaction evidence, false for archive metadata.</param>
    /// <param name="action">The legacy action that must not execute.</param>
    /// <returns>The completed test.</returns>
    [TestCase(false, InstallPlanAction.Uninstall)]
    [TestCase(false, InstallPlanAction.RevertToStock)]
    [TestCase(false, InstallPlanAction.Prepatch)]
    [TestCase(false, InstallPlanAction.ApplyPatchFile)]
    [TestCase(true, InstallPlanAction.Uninstall)]
    [TestCase(true, InstallPlanAction.Prepatch)]
    public async Task InstallPlanStopsBeforeAnySideEffect(bool pending, InstallPlanAction action)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        if (pending)
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "");
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, "winhttp.dll"), "old loader");
        var before = Snapshot(fileSystem);
        var cache = new Mock<ICacheService>(MockBehavior.Strict);
        var downloads = new Mock<IPatchDownloadService>(MockBehavior.Strict);
        var conversion = new Mock<IBundleConversionService>(MockBehavior.Strict);
        var locks = new Mock<IInstallFileLockService>(MockBehavior.Strict);
        var disk = new Mock<IDiskSpaceService>(MockBehavior.Strict);
        var config = new Mock<ILauncherConfigService>(MockBehavior.Strict);
        var service = new InstallPlanService(fileSystem, cache.Object, new MockEnvironmentProvider(),
            Mock.Of<IAssemblyService>(), Mock.Of<IModuleDefinitionService>(), new ZipFileService(fileSystem),
            disk.Object, downloads.Object, config.Object, State(fileSystem), conversion.Object, locks.Object);
        var plan = new InstallPlan();
        bool downloaded = false;
        switch (action)
        {
            case InstallPlanAction.Uninstall:
                plan.Uninstall();
                break;
            case InstallPlanAction.RevertToStock:
                plan.RevertToStock();
                break;
            case InstallPlanAction.Prepatch:
                plan.Prepatch();
                break;
            default:
                plan.ApplyPatchFile((_, _, _) =>
                {
                    downloaded = true;
                    return Task.FromResult("unused.patch");
                }, Ksp2Redux.Tools.Launcher.Localization.LogMessage.Literal("must not download"));
                break;
        }

        await Assert.ThrowsAsync<IOException>(() => service.ApplyToFolder(plan, INSTALL_ROOT, _ => { },
            (_, _) => { }, (_, _) => { }, CancellationToken.None));

        Assert.That(downloaded, Is.False);
        Assert.That(downloads.Invocations, Is.Empty);
        Assert.That(conversion.Invocations, Is.Empty);
        Assert.That(cache.Invocations, Is.Empty);
        Assert.That(locks.Invocations, Is.Empty);
        Assert.That(disk.Invocations, Is.Empty);
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Direct bundle operations preserve journals and bytes in full-source installations.
    /// </summary>
    /// <param name="convert">True for conversion, false for journal reversal.</param>
    [TestCase(true)]
    [TestCase(false)]
    public void DirectBundleMutationIsBlocked(bool convert)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        var before = Snapshot(fileSystem);
        var service = new BundleConversionService(fileSystem, State(fileSystem));

        Assert.Throws<IOException>(() =>
        {
            if (convert)
                service.Convert(INSTALL_ROOT, _ => { }, CancellationToken.None);
            else
                service.Revert(INSTALL_ROOT, _ => { }, CancellationToken.None);
        });
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Startup and setting changes leave managed boot bytes intact and report skipped writes.
    /// </summary>
    /// <param name="protection">The protected evidence shape or a legacy control.</param>
    [TestCase("pending")]
    [TestCase("archive")]
    [TestCase("corrupt")]
    [TestCase("legacy")]
    public void BootConfigWritesRespectInstallationState(string protection)
    {
        var fileSystem = CreateFileSystem();
        string boot = fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64_Data", "boot.config");
        byte[] original = Encoding.UTF8.GetBytes("gfx-enable-gfx-jobs=1\r\nhdr-display-enabled=0\r\nunknown=42\r\n");
        fileSystem.File.WriteAllBytes(boot, original);
        if (protection == "pending")
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "{");
        if (protection == "archive")
            WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        if (protection == "corrupt")
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip"), "broken");
        var config = new LauncherConfig("unused.json");
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(config);
        var log = new Mock<ILogService>();
        var service = new Ksp2InstallService(configuration.Object, fileSystem,
            Mock.Of<IModuleDefinitionService>(), log.Object, State(fileSystem));
        var entry = service.AddInstall(fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe"));

        Assert.DoesNotThrow(service.ApplyActiveInstallBootConfig);
        service.UpdateInstallDisableGraphicsJobs(entry.Id, true);

        if (protection == "legacy")
        {
            Assert.That(fileSystem.File.ReadAllText(boot), Does.Contain("gfx-enable-gfx-jobs=0"));
            Assert.That(fileSystem.File.ReadAllText(boot), Does.Contain("hdr-display-enabled=0"));
        }
        else
        {
            Assert.That(fileSystem.File.ReadAllBytes(boot), Is.EqualTo(original));
            log.Verify(candidate => candidate.Warn(It.Is<string>(text => text.Contains("Skipped boot.config")),
                It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
        }
    }

    /// <summary>
    /// Recursive install deletion also preserves stock archives and pending markers.
    /// </summary>
    /// <param name="pending">True for pending state, false for a completed full archive.</param>
    /// <returns>The completed test.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task DirectInstallDeletionPreservesProtectedEvidence(bool pending)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        if (pending)
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "");
        var before = Snapshot(fileSystem);
        var service = new Ksp2GameUninstallService(fileSystem, Mock.Of<ILogService>(), State(fileSystem));

        var removal = service.Inspect(fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe"));
        Assert.That(removal.Kind, Is.EqualTo(Ksp2GameRemovalKind.Protected));
        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(new Ksp2GameRemoval(Ksp2GameRemovalKind.DeleteFolder, INSTALL_ROOT)));
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Self-update checks every configured identity while preserving superseded binaries.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Test]
    public async Task CliSelfUpdateIsBlockedByAnInactivePendingInstallBeforeCleanup()
    {
        var harness = new CliCommandHarness();
        harness.Installs.AddInstall(harness.CreateGame(@"C:\Games\Active"));
        harness.Installs.AddInstall(harness.CreateGame(INSTALL_ROOT), "Pending");
        string marker = harness.FileSystem.Path.Combine(INSTALL_ROOT, MARKER);
        harness.FileSystem.File.WriteAllText(marker, "");
        harness.Environment.ProcessPath = @"C:\Tools\redux-launcher-cli.exe";
        harness.FileSystem.Directory.CreateDirectory(@"C:\Tools");
        string superseded = harness.Environment.ProcessPath + SelfUpdateCommand.SUPERSEDED_SUFFIX;
        harness.FileSystem.File.WriteAllText(superseded, "previous executable");

        int exit = await new SelfUpdateCommand().RunWithContextAsync(harness.Context,
            new SelfUpdateSettings { AssumeYes = true });

        Assert.That(exit, Is.EqualTo(ExitCode.SELF_UPDATE_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("unfinished"));
        Assert.That(harness.FileSystem.File.ReadAllText(superseded), Is.EqualTo("previous executable"));
        Assert.That(harness.FileSystem.File.ReadAllText(marker), Is.Empty);
    }

    /// <summary>
    /// GUI self-update returns a blocked result before networking or update-directory cleanup.
    /// </summary>
    /// <returns>The completed test.</returns>
    [Test]
    public async Task GuiSelfUpdatePreservesPendingInstallAndUpdateFiles()
    {
        var fileSystem = CreateFileSystem();
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "");
        var before = Snapshot(fileSystem);
        var config = new LauncherConfig("unused.json");
        config.Ksp2Installs.Add(new Ksp2InstallEntry { ExePath = fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe") });
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(config);
        var environment = new MockEnvironmentProvider();
        var assembly = new Mock<IAssemblyService>();
        assembly.Setup(service => service.GetName()).Returns(new System.Reflection.AssemblyName("Test"));
        var log = new Mock<ILogService>();
        var update = new UpdateService(configuration.Object, fileSystem, environment, assembly.Object,
            Mock.Of<IMessageBoxService>(), log.Object, State(fileSystem));

        Assert.That(await update.CheckAndPerformUpdateAsync(), Is.False);

        log.Verify(candidate => candidate.Warn(It.Is<string>(text => text.Contains("self-update is blocked")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Early self-update stages inspect persisted identities without repairing configuration.
    /// </summary>
    /// <param name="legacyIdentity">True for a pre-profile configuration path.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void EarlySelfUpdateRejectsPersistedPendingIdentity(bool legacyIdentity)
    {
        var fileSystem = CreateFileSystem();
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "garbage");
        var config = new LauncherConfig("unused.json");
        string exe = fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe");
        if (legacyIdentity)
            config.Ksp2InstallPath = exe;
        else
            config.Ksp2Installs.Add(new Ksp2InstallEntry { ExePath = exe });
        string path = fileSystem.Path.Combine(INSTALL_ROOT, "launcher-config.json");
        fileSystem.File.WriteAllText(path, JsonSerializer.Serialize(config));
        var before = Snapshot(fileSystem);

        Assert.Throws<IOException>(() => State(fileSystem).EnsureSelfUpdateAllowedFromFile(path));

        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Early updater rejection returns a blocked result without offering partial-update deletion.
    /// </summary>
    [Test]
    public void LauncherStartupUpdateBoundaryPreservesEvidenceAndReportsBlockedState()
    {
        var fileSystem = CreateFileSystem();
        var config = new LauncherConfig("unused.json");
        config.Ksp2Installs.Add(new Ksp2InstallEntry { ExePath = fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe") });
        string path = fileSystem.Path.Combine(INSTALL_ROOT, "config.json");
        fileSystem.File.WriteAllText(path, JsonSerializer.Serialize(config));
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "");
        var before = Snapshot(fileSystem);
        try
        {
            Assert.That(Program.CheckSelfUpdateState(State(fileSystem), path), Is.False);
            Assert.That(Program.SelfUpdateBlockReason, Does.Contain("unfinished installation transaction"));
            Assert.That(Program.PartialUpdate, Is.False);
            AssertUnchanged(fileSystem, before);
        }
        finally
        {
            Program.CheckSelfUpdateState(State(fileSystem), fileSystem.Path.Combine(INSTALL_ROOT, "missing-config.json"));
        }
    }

    /// <summary>
    /// Completed full archives allow launch and self-update without opening manifest payloads.
    /// </summary>
    [Test]
    public void CompletedFullArchiveDoesNotBlockLaunchOrSelfUpdate()
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        var config = new LauncherConfig("unused.json");
        config.Ksp2Installs.Add(new Ksp2InstallEntry { ExePath = fileSystem.Path.Combine(INSTALL_ROOT, "KSP2_x64.exe") });
        var before = Snapshot(fileSystem);

        Assert.DoesNotThrow(() => State(fileSystem).EnsureLaunchAllowed(INSTALL_ROOT));
        Assert.DoesNotThrow(() => State(fileSystem).EnsureSelfUpdateAllowed(config));
        Assert.That(State(fileSystem).Inspect(INSTALL_ROOT).LegacyMutationBlockReason, Does.Contain("not been authenticated"));
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Full-archive marker payloads never need to be decompressed or parsed for protection.
    /// </summary>
    [Test]
    public void MarkerPresenceProtectsCacheWithoutReadingItsPayload()
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        var before = Snapshot(fileSystem);
        var entry = new Mock<IZipArchiveEntry>(MockBehavior.Strict);
        entry.SetupGet(candidate => candidate.FullName).Returns(StockBaselineManifest.MANIFEST_PATH);
        var archive = new Mock<IZipArchive>();
        archive.SetupGet(candidate => candidate.Entries).Returns([entry.Object]);
        var zip = new Mock<IZipFileService>();
        zip.Setup(service => service.NewArchive(It.IsAny<Stream>(), ZipArchiveMode.Read, true)).Returns(archive.Object);
        var service = new CacheService(fileSystem, zip.Object, new InstallationStateService(fileSystem, zip.Object));

        Assert.Throws<IOException>(() => service.RecursivelyCreateCache(INSTALL_ROOT));

        entry.Verify(candidate => candidate.Open(), Times.Never);
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Read-only guard behavior works with each launcher host's filesystem semantics.
    /// </summary>
    /// <param name="mode">The simulated launcher host.</param>
    [TestCase(SimulationMode.Windows)]
    [TestCase(SimulationMode.Linux)]
    [TestCase(SimulationMode.MacOS)]
    public void GuardProtectsWindowsAndPosixRootsWithoutNativeTransactionAdapters(SimulationMode mode)
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(mode));
        string root = mode == SimulationMode.Windows ? @"C:\Games\Boundary" : "/games/boundary";
        fileSystem.Directory.CreateDirectory(root);
        string marker = fileSystem.Path.Combine(root, MARKER);
        fileSystem.File.WriteAllText(marker, "");
        var state = State(fileSystem);

        Assert.Throws<IOException>(() => new CacheService(fileSystem, new ZipFileService(fileSystem), state)
            .RecursivelyRestoreCache(root));
        Assert.Throws<IOException>(() => state.EnsureLaunchAllowed(root));
        Assert.That(fileSystem.File.ReadAllText(marker), Is.Empty);
    }

    /// <summary>
    /// Invalid persisted configuration blocks early replacement while preserving its bytes.
    /// </summary>
    /// <param name="json">The invalid configuration.</param>
    [TestCase("{")]
    [TestCase("null")]
    [TestCase("{\"Ksp2Installs\":null}")]
    [TestCase("{\"Ksp2Installs\":[null]}")]
    [TestCase("{\"Ksp2InstallPath\":\"relative/game.exe\"}")]
    public void EarlySelfUpdatePreservesMalformedConfiguration(string json)
    {
        var fileSystem = CreateFileSystem();
        string config = fileSystem.Path.Combine(INSTALL_ROOT, "config.json");
        fileSystem.File.WriteAllText(config, json);
        var before = Snapshot(fileSystem);

        Assert.Throws<IOException>(() => State(fileSystem).EnsureSelfUpdateAllowedFromFile(config));

        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Steam download entry rejects protected installs before Steam calls or writes.
    /// </summary>
    /// <param name="pending">True for pending state, false for a full archive.</param>
    /// <returns>The completed test.</returns>
    [TestCase(true)]
    [TestCase(false)]
    public async Task SteamDownloadCannotOverwriteProtectedInstallation(bool pending)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        if (pending)
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(INSTALL_ROOT, MARKER), "");
        var before = Snapshot(fileSystem);
        var session = new Mock<ISteamSessionService>(MockBehavior.Strict);
        var service = new SteamDepotDownloader(session.Object, fileSystem, Mock.Of<ILogService>(), State(fileSystem));

        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(SteamDepotDownloader.KSP2_APP_ID,
            INSTALL_ROOT, Mock.Of<IProgress<SteamDownloadProgress>>(), CancellationToken.None));

        Assert.That(session.Invocations, Is.Empty);
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Access failures inspecting the marker stop cache mutation without trusting File.Exists.
    /// </summary>
    [Test]
    public void UnreadableMarkerStateBlocksBeforeCacheMutation()
    {
        var fileSystem = CreateFileSystem();
        var file = new Mock<IFile>();
        file.Setup(candidate => candidate.GetAttributes(It.IsAny<string>()))
            .Throws(new UnauthorizedAccessException("marker metadata is unreadable"));
        var blockedFileSystem = new Mock<IFileSystem>();
        blockedFileSystem.SetupGet(candidate => candidate.File).Returns(file.Object);
        blockedFileSystem.SetupGet(candidate => candidate.Path).Returns(fileSystem.Path);
        var state = new InstallationStateService(blockedFileSystem.Object, new ZipFileService(fileSystem));
        var before = Snapshot(fileSystem);

        var error = Assert.Throws<IOException>(() => new CacheService(fileSystem, new ZipFileService(fileSystem), state)
            .RecursivelyCreateCache(INSTALL_ROOT));

        Assert.That(error!.Message, Does.Contain("could not be inspected"));
        Assert.Throws<IOException>(() => state.EnsureLaunchAllowed(INSTALL_ROOT));
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// ZIP64 metadata claims are protected while inconsistent classic metadata fails closed.
    /// </summary>
    /// <param name="inconsistent">True for conflicting classic and ZIP64 counts.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void Zip64ArchiveEvidenceSurvivesLegacyCacheCreation(bool inconsistent)
    {
        var fileSystem = CreateFileSystem();
        WriteArchive(fileSystem, StockBaselineManifest.MANIFEST_PATH);
        string path = fileSystem.Path.Combine(INSTALL_ROOT, "uninstall.zip");
        byte[] old = fileSystem.File.ReadAllBytes(path);
        int oldEnd = old.Length - 22;
        var bytes = new byte[old.Length + 76];
        old.AsSpan(0, oldEnd).CopyTo(bytes);
        var zip64 = bytes.AsSpan(oldEnd, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(zip64, 0x06064B50);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[4..], 44);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[32..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[40..], BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(oldEnd + 12)));
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[48..], BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(oldEnd + 16)));
        var locator = bytes.AsSpan(oldEnd + 56, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064B50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)oldEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        old.AsSpan(oldEnd).CopyTo(bytes.AsSpan(oldEnd + 76));
        var end = bytes.AsSpan(oldEnd + 76);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], inconsistent ? (ushort)2 : ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], inconsistent ? (ushort)2 : ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], uint.MaxValue);
        fileSystem.File.WriteAllBytes(path, bytes);
        var before = Snapshot(fileSystem);

        var error = Assert.Throws<IOException>(() => new CacheService(fileSystem, new ZipFileService(fileSystem), State(fileSystem))
            .RecursivelyCreateCache(INSTALL_ROOT));

        Assert.That(error!.Message, Does.Contain(inconsistent ? "disagree" : "claims a full stock backup"));
        AssertUnchanged(fileSystem, before);
    }

    /// <summary>
    /// Ordinary legacy snapshot creation and restore still operate without new evidence.
    /// </summary>
    [Test]
    public void OrdinaryLegacyCacheStillCreatesAndRestores()
    {
        var fileSystem = new RealFileSystem();
        string root = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "redux-boundary-" + Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(root);
        try
        {
            string original = fileSystem.Path.Combine(root, "original.txt");
            fileSystem.File.WriteAllText(original, "original bytes");
            string streaming = fileSystem.Path.Combine(root, "KSP2_x64_Data", "StreamingAssets");
            fileSystem.Directory.CreateDirectory(streaming);
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(streaming, "stock.bundle"), "bundle bytes");
            var zip = new ZipFileService(fileSystem);
            var service = new CacheService(fileSystem, zip, new InstallationStateService(fileSystem, zip));

            service.RecursivelyCreateCache(root);
            fileSystem.File.WriteAllText(original, "patched bytes");
            service.RecursivelyRestoreCache(root);

            Assert.That(fileSystem.File.ReadAllText(original), Is.EqualTo("original bytes"));
            Assert.That(fileSystem.File.ReadAllText(fileSystem.Path.Combine(streaming, "stock.bundle")), Is.EqualTo("bundle bytes"));
            Assert.That(fileSystem.File.Exists(fileSystem.Path.Combine(root, "uninstall.zip")), Is.False);
        }
        finally
        {
            fileSystem.Directory.Delete(root, true);
        }
    }
}
