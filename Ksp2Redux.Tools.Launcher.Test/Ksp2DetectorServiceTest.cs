using Ksp2Redux.Tools.Launcher.Services;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

public class Ksp2DetectorServiceTest
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";

    private static (Ksp2DetectorService Service, MockFileSystem FileSystem, Mock<ILogService> Log) MakeService()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var env = new MockEnvironmentProvider();
        var os = new Mock<IOperatingSystemService>();
        os.Setup(o => o.IsLinux()).Returns(false);
        var log = new Mock<ILogService>();
        fs.Directory.CreateDirectory(SteamRoot);
        return (new Ksp2DetectorService(fs, env, os.Object, log.Object), fs, log);
    }

    [Test]
    public void DetectKsp2InstallLocation_LibraryFoldersVdfExistsButParsesToNothing_LogsAWarning()
    {
        var (service, fs, log) = MakeService();
        // A file caught mid-write by Steam - present, but the regex finds no "path" entries in it.
        fs.Directory.CreateDirectory(fs.Path.Combine(SteamRoot, "steamapps"));
        fs.File.WriteAllText(fs.Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"), "not valid vdf content");

        service.DetectKsp2InstallLocation();

        log.Verify(l => l.Warn(
            It.Is<string>(s => s.Contains("libraryfolders.vdf") && s.Contains("no library paths")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void DetectKsp2InstallLocation_AppManifestExistsButInstallDirUnparseable_LogsAWarning()
    {
        var (service, fs, log) = MakeService();
        fs.Directory.CreateDirectory(fs.Path.Combine(SteamRoot, "steamapps"));
        fs.File.WriteAllText(fs.Path.Combine(SteamRoot, "steamapps", "appmanifest_954850.acf"), "not valid acf content");

        service.DetectKsp2InstallLocation();

        log.Verify(l => l.Warn(
            It.Is<string>(s => s.Contains("appmanifest_954850.acf") && s.Contains("installdir")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void DetectKsp2InstallLocation_NoLibraryFoldersVdfAtAll_DoesNotLogAWarning()
    {
        // Not having extra libraries configured is the normal case and shouldn't look like a parse failure.
        var (service, fs, log) = MakeService();

        service.DetectKsp2InstallLocation();

        log.Verify(l => l.Warn(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
