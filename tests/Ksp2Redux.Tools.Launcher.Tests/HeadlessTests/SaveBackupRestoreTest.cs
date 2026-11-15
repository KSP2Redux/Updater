using System.IO.Compression;
using Avalonia.Headless.NUnit;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class SaveBackupRestoreTest
{
    private const string SAVE = @"C:\Users\Jeb\AppData\LocalLow\Intercept Games\Kerbal Space Program 2\Saves\SinglePlayer\Default\autosave_1.json";

    private static SettingsTabViewModel StartWithABackup()
    {
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsMacOS()).Returns(false);
        TestAppBuilder.EnvironmentProvider.SetFolderPath(Environment.SpecialFolder.UserProfile, @"C:\Users\Jeb");
        TestHelpers.MockMessageBoxAcceptAll();
        var fileSystem = TestAppBuilder.FileSystem;
        var zips = new ZipFileService(fileSystem);
        TestAppBuilder.ZipFileService.Setup(z => z.NewArchive(It.IsAny<Stream>(), It.IsAny<ZipArchiveMode>(), It.IsAny<bool>()))
            .Returns((Stream stream, ZipArchiveMode mode, bool leaveOpen) => zips.NewArchive(stream, mode, leaveOpen));
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(SAVE)!);
        fileSystem.File.WriteAllText(SAVE, "before the update");
        fileSystem.File.SetLastWriteTimeUtc(SAVE, DateTime.UtcNow.AddHours(-1));
        TestAppBuilder.ServiceProvider.GetRequiredService<ISaveBackupService>().BackUp();
        fileSystem.File.WriteAllText(SAVE, "after the update");

        var settings = TestAppBuilder.ServiceProvider.GetRequiredService<SettingsTabViewModel>();
        settings.RefreshSaveBackups();
        return settings;
    }

    [AvaloniaTest]
    public async Task RestoreSaveBackup_WhileKsp2Runs_LeavesTheSavesAlone()
    {
        var settings = StartWithABackup();
        TestAppBuilder.GameProcessService.Setup(g => g.IsRunning()).Returns(true);

        await settings.RestoreSaveBackup();

        Assert.That(TestAppBuilder.FileSystem.File.ReadAllText(SAVE), Is.EqualTo("after the update"));
    }

    [AvaloniaTest]
    public async Task RestoreSaveBackup_Confirmed_PutsTheBackupBack()
    {
        var settings = StartWithABackup();
        Assert.That(settings.SaveBackups, Has.Count.EqualTo(1));

        await settings.RestoreSaveBackup();

        Assert.That(TestAppBuilder.FileSystem.File.ReadAllText(SAVE), Is.EqualTo("before the update"));
        Assert.That(settings.SaveBackups, Has.Count.EqualTo(2), "The saves from before the restore are kept as a backup.");
    }
}
