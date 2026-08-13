using EnvironmentAbstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class GameDataFolderServiceTest
{
    private static GameDataFolderService Build(bool isLinux, string userProfile = @"C:\Users\Eivind")
    {
        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(
            isLinux ? SimulationMode.Linux : SimulationMode.Windows));

        var environment = new Mock<IEnvironmentProvider>();
        environment.Setup(e => e.GetFolderPath(Environment.SpecialFolder.UserProfile)).Returns(userProfile);

        var operatingSystem = new Mock<IOperatingSystemService>();
        operatingSystem.Setup(o => o.IsLinux()).Returns(isLinux);

        return new GameDataFolderService(fileSystem, environment.Object, operatingSystem.Object);
    }

    private static Ksp2InstallEntry Entry(string exePath, string steamAppId = "954850") =>
        new() { ExePath = exePath, SteamAppId = steamAppId };

    // LocalLow has no SpecialFolder of its own, so it is built off the user profile.
    [Test]
    public void Resolve_OnWindows_IsTheLocalLowFolder()
    {
        // Arrange
        GameDataFolderService service = Build(isLinux: false);

        // Act
        string? folder = service.Resolve(Entry(@"C:\Games\KSP2\KSP2_x64.exe"));

        // Assert
        Assert.That(folder, Is.EqualTo(@"C:\Users\Eivind\AppData\LocalLow\Intercept Games\Kerbal Space Program 2"));
    }

    // The install is not consulted on Windows, so a machine with no install configured still gets a
    // folder to open.
    [Test]
    public void Resolve_OnWindowsWithNoInstall_StillResolves()
    {
        // Arrange
        GameDataFolderService service = Build(isLinux: false);

        // Act
        string? folder = service.Resolve(null);

        // Assert
        Assert.That(folder, Does.EndWith("Kerbal Space Program 2"));
    }

    // Under Proton the game writes into the Steam prefix beside the install rather than into the
    // real home directory.
    [Test]
    public void Resolve_OnLinux_IsInsideTheProtonPrefixBesideTheInstall()
    {
        // Arrange
        GameDataFolderService service = Build(isLinux: true);

        // Act
        string? folder = service.Resolve(Entry("/home/eivind/.steam/steam/steamapps/common/Kerbal Space Program 2/KSP2_x64.exe"));

        // Assert
        Assert.That(folder, Is.EqualTo(
            "/home/eivind/.steam/steam/steamapps/compatdata/954850/pfx/drive_c/users/steamuser/AppData/LocalLow/Intercept Games/Kerbal Space Program 2"));
    }

    [Test]
    public void Resolve_OnLinuxWithADifferentAppId_UsesThatPrefix()
    {
        // Arrange
        GameDataFolderService service = Build(isLinux: true);

        // Act
        string? folder = service.Resolve(Entry("/games/steamapps/common/KSP2/KSP2_x64.exe", steamAppId: "123456"));

        // Assert
        Assert.That(folder, Does.Contain("/compatdata/123456/pfx/"));
    }

    // An install outside a steamapps folder has no prefix to look in, and guessing would send the
    // user somewhere that does not exist.
    [Test]
    public void Resolve_OnLinuxOutsideSteam_FindsNothing()
    {
        // Arrange
        GameDataFolderService service = Build(isLinux: true);

        // Act
        string? folder = service.Resolve(Entry("/opt/ksp2/KSP2_x64.exe"));

        // Assert
        Assert.That(folder, Is.Null);
    }

    [Test]
    public void Resolve_OnLinuxWithNoInstall_FindsNothing()
    {
        // Arrange
        GameDataFolderService service = Build(isLinux: true);

        // Act
        string? folder = service.Resolve(null);

        // Assert
        Assert.That(folder, Is.Null);
    }
}
