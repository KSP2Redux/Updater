using Avalonia.Controls;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Moq;
using MsBox.Avalonia.Enums;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.ViewModels;

public class Ksp2InstallRowViewModelValidationTest
{
    private const string ValidExePath = @"C:\Games\Ksp2\KSP2_x64.exe";

    private static (Ksp2InstallRowViewModel Row, MockFileSystem FileSystem) MakeRow(string exePath, string steamAppId)
    {
        var (row, fs, _, _) = MakeRowWithMocks(exePath, steamAppId, "beta");
        return (row, fs);
    }

    private static (Ksp2InstallRowViewModel Row, MockFileSystem FileSystem, Mock<IMessageBoxService> MessageBox, Ksp2InstallEntry Entry)
        MakeRowWithMocks(string exePath, string steamAppId, string releaseChannel)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(@"C:\Games\Ksp2");
        fs.File.WriteAllBytes(ValidExePath, [0x00]);

        var entry = new Ksp2InstallEntry
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            ExePath = exePath,
            ReleaseChannel = releaseChannel,
            SteamAppId = steamAppId
        };
        var ksp2InstallService = new Mock<IKsp2InstallService>();
        var messageBoxService = new Mock<IMessageBoxService>();
        var row = new Ksp2InstallRowViewModel(fs, ksp2InstallService.Object, messageBoxService.Object, entry, isActive: true);
        return (row, fs, messageBoxService, entry);
    }

    private static void VerifyBetaWarningCount(Mock<IMessageBoxService> messageBox, Times times)
        => messageBox.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            It.Is<string>(t => t.Contains("Beta")),
            It.Is<string>(b => b.Contains("QA testing")),
            It.IsAny<ButtonEnum>(),
            It.IsAny<Icon>(),
            It.IsAny<object?>(),
            It.IsAny<WindowStartupLocation>()), times);

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

    [Test]
    public void ReleaseChannel_SwitchedToBeta_WarnsThatBetaIsForQaTesting()
    {
        var (row, _, messageBox, _) = MakeRowWithMocks(ValidExePath, "954850", "stable");

        row.ReleaseChannel = "beta";

        VerifyBetaWarningCount(messageBox, Times.Once());
    }

    [Test]
    public void ReleaseChannel_AlreadyBetaOnConstruction_DoesNotWarn()
    {
        var (_, _, messageBox, _) = MakeRowWithMocks(ValidExePath, "954850", "beta");

        VerifyBetaWarningCount(messageBox, Times.Never());
    }

    [Test]
    public void ReleaseChannel_SwitchedAwayFromBeta_DoesNotWarn()
    {
        var (row, _, messageBox, _) = MakeRowWithMocks(ValidExePath, "954850", "beta");

        row.ReleaseChannel = "stable";

        VerifyBetaWarningCount(messageBox, Times.Never());
    }

    [Test]
    public void ReleaseChannel_ClearedByComboBoxThenRestoredToBeta_DoesNotWarn()
    {
        var (row, _, messageBox, _) = MakeRowWithMocks(ValidExePath, "954850", "beta");

        row.ReleaseChannel = "";
        row.ReleaseChannel = "beta";

        VerifyBetaWarningCount(messageBox, Times.Never());
    }
}
