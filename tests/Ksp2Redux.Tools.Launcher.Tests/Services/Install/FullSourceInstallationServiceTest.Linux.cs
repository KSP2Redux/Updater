using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    /// <summary>
    /// The real native Linux coordinator publishes, changes channel, repairs and restores its complete fixture stock.
    /// </summary>
    /// <returns>The native fixture task.</returns>
    [Test, Platform("Linux")]
    public async Task NativeLinux_CoordinatorInstallChannelRepairUninstallAsync()
    {
        string root = SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_LINUX_FIXTURE_ROOT")! + "-coordinator";
        var fs = new RealFileSystem();
        Assert.That(fs.Path.Exists(root), Is.False);
        fs.Directory.CreateDirectory(root);
        string game = fs.Path.Combine(root, "game");
        string original = fs.Path.Combine(root, "stock");
        string source = fs.Path.Combine(root, "target");
        var oldFiles = new Dictionary<string, string>
        {
            [Ksp2Install.KSP2_EXE_NAME] = "owned stock executable",
            ["KSP2_x64_Data/boot.config"] = "fixture=stock\n",
            ["KSP2_x64_Data/StreamingAssets/content.bin"] = "owned stock content"
        };
        var targetFiles = oldFiles.ToDictionary(pair => pair.Key, pair => pair.Value.Replace("stock", "target", StringComparison.Ordinal));
        StockBaselineManifest stock = CoordinatorLinuxTree(fs, original, "owned-linux-original", oldFiles);
        _ = CoordinatorLinuxTree(fs, game, "owned-linux-original", oldFiles);
        StockBaselineManifest target = CoordinatorLinuxTree(fs, source, "owned-linux-target", targetFiles);
        string user = fs.Path.Combine(game, "mods", "user.txt");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(user)!);
        fs.File.WriteAllText(user, "unknown user bytes");
        var entry = new Ksp2InstallEntry { ExePath = fs.Path.Combine(game, Ksp2Install.KSP2_EXE_NAME), Name = "Owned native Linux" };
        var config = new LauncherConfig { Ksp2Installs = [entry], ActiveKsp2InstallId = entry.Id, Feeds = [] };
        var configuration = new Mock<ILauncherConfigService>();
        configuration.SetupGet(service => service.Config).Returns(config);
        string storage = fs.Path.Combine(root, "launcher");
        fs.Directory.CreateDirectory(storage);
        configuration.Setup(service => service.GetLocalStorageDirectory()).Returns(storage);
        var preparation = new Mock<IFullSourcePreparationService>();
        preparation.Setup(service => service.VerifyTargetAsync(It.IsAny<FullSourcePreparedTarget>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        preparation.Setup(service => service.VerifyStockAsync(It.IsAny<FullSourceStockPreparation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var space = new Mock<IDiskSpaceService>();
        space.Setup(service => service.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var processes = new Mock<IGameProcessService>(MockBehavior.Strict);
        processes.Setup(service => service.IsRunning(game)).Returns(false);
        var os = new OperatingSystemService();
        FullSourceInstallationService Coordinator() => new(fs, configuration.Object, new ZipFileService(fs), space.Object,
            preparation.Object, os, Mock.Of<ILogService>(), processes.Object, new InstallFileLockService(fs, os));
        var identity = new FullSourceReleaseIdentity("owned-linux-release", "beta", "2.0.0.1.200",
            Convert.ToHexString(SHA256.HashData(target.ToJson())), "fixture:explicit-native-linux-authority");
        var prepared = new FullSourcePreparedTarget(identity, target, source, false);
        var baseline = new FullSourceStockPreparation(stock, original, stock, [], [], entry.Id, identity);
        await Coordinator().ApplyAsync(entry.Id, prepared, baseline, false, _ => { }, CancellationToken.None);
        string archive = fs.Path.Combine(game, "uninstall.zip");
        byte[] originalArchive = fs.File.ReadAllBytes(archive);
        Assert.That(Coordinator().GetPublishedIdentity(entry.Id)!.Channel, Is.EqualTo("beta"));
        var stable = prepared with { Identity = identity with { Channel = "stable", ReleaseId = "owned-linux-stable" } };
        await Coordinator().ApplyAsync(entry.Id, stable, null, false, _ => { }, CancellationToken.None);
        Assert.That(Coordinator().GetPublishedIdentity(entry.Id)!.Channel, Is.EqualTo("stable"));
        fs.File.WriteAllText(fs.Path.Combine(game, "KSP2_x64_Data/StreamingAssets/content.bin"), "observed fixture damage");
        await Coordinator().ApplyAsync(entry.Id, stable, null, true, _ => { }, CancellationToken.None);
        Assert.That(fs.File.ReadAllText(fs.Path.Combine(game, "KSP2_x64_Data/StreamingAssets/content.bin")), Is.EqualTo(targetFiles["KSP2_x64_Data/StreamingAssets/content.bin"]));
        await Coordinator().UninstallAsync(entry.Id, _ => { }, CancellationToken.None);
        foreach (var file in oldFiles)
        {
            Assert.That(fs.File.ReadAllText(fs.Path.Combine(game, file.Key)), Is.EqualTo(file.Value));
        }
        Assert.That(fs.File.ReadAllBytes(archive), Is.EqualTo(originalArchive));
        Assert.That(fs.File.ReadAllText(user), Is.EqualTo("unknown user bytes"));
        fs.File.WriteAllText(fs.Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
        {
            Success = true, NativeLinux = true, ProductionCoordinatorSource = true,
            InstallChannelRepairOfflineUninstall = true, CompleteFixtureArchiveIncludesStreamingAssets = true,
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(originalArchive)), UnknownUserBytesPreserved = true,
            FullGame = false, RealPublisherAuthentication = false, GameProcessCallsAreDoubles = true,
            RuntimeQualification = "Installed .NET 8 compatibility target, not shipped .NET 10 launcher process"
        }));
    }

    private static StockBaselineManifest CoordinatorLinuxTree(RealFileSystem fs, string root, string identity, Dictionary<string, string> files)
    {
        var rows = new List<StockBaselineFile>();
        foreach (var file in files)
        {
            string path = fs.Path.Combine(root, file.Key);
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
            fs.File.WriteAllText(path, file.Value);
            int mode = file.Key == Ksp2Install.KSP2_EXE_NAME ? 493 : 420;
            StockArchive.ApplyUnixPermissions(fs, path, mode);
            rows.Add(new StockBaselineFile(file.Key, Encoding.UTF8.GetByteCount(file.Value),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))), mode));
        }
        var paths = new StockBaselineManifest(identity, "Owned native Linux fixture", rows.Select(file => file with { UnixMode = null }));
        foreach (string directory in paths.Directories)
        {
            StockArchive.ApplyUnixPermissions(fs, fs.Path.Combine(root, directory), 493);
        }
        return new StockBaselineManifest(identity, "Owned native Linux fixture", rows,
            unixDirectoryModes: paths.Directories.ToDictionary(path => path, _ => 493, StringComparer.Ordinal));
    }
}
