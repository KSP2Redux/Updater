using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

public class Ksp2InstallRowViewModelValidationTest
{
    private const string ValidExePath = @"C:\Games\Ksp2\KSP2_x64.exe";

    private static (Ksp2InstallRowViewModel Row, MockFileSystem FileSystem) MakeRow(string exePath, string steamAppId)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(@"C:\Games\Ksp2");
        fs.File.WriteAllBytes(ValidExePath, [0x00]);

        var entry = new Ksp2InstallEntry
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            ExePath = exePath,
            ReleaseChannel = "beta",
            SteamAppId = steamAppId,
        };
        var ksp2InstallService = new Mock<IKsp2InstallService>();
        var row = new Ksp2InstallRowViewModel(fs, ksp2InstallService.Object, entry, isActive: true);
        return (row, fs);
    }

    [Test]
    public void ExePath_PointsToRealKsp2Exe_HasNoError()
    {
        var (row, _) = MakeRow(ValidExePath, "954850");
        Assert.That(row.ExePathError, Is.Null);
    }

    [Test]
    public void ExePath_WrongFileName_HasError()
    {
        var (row, fs) = MakeRow(ValidExePath, "954850");
        fs.File.WriteAllBytes(@"C:\Games\Ksp2\NotTheGame.exe", [0x00]);

        row.ExePath = @"C:\Games\Ksp2\NotTheGame.exe";

        Assert.That(row.ExePathError, Does.Contain("KSP2_x64.exe"));
    }

    [Test]
    public void ExePath_DoesNotExist_HasError()
    {
        var (row, _) = MakeRow(ValidExePath, "954850");

        row.ExePath = @"C:\Games\Ksp2\Missing\KSP2_x64.exe";

        Assert.That(row.ExePathError, Does.Contain("doesn't exist"));
    }

    [Test]
    public void SteamAppId_Numeric_HasNoError()
    {
        var (row, _) = MakeRow(ValidExePath, "954850");
        Assert.That(row.SteamAppIdError, Is.Null);
    }

    [Test]
    public void SteamAppId_Empty_HasNoError()
    {
        var (row, _) = MakeRow(ValidExePath, "");
        Assert.That(row.SteamAppIdError, Is.Null);
    }

    [Test]
    public void SteamAppId_NonNumeric_HasError()
    {
        var (row, _) = MakeRow(ValidExePath, "954850");

        row.SteamAppId = "not-a-number";

        Assert.That(row.SteamAppIdError, Does.Contain("numeric"));
    }
}
