using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests;

public class LauncherConfigServiceTest
{
    private string simpleLauncherConfigJson = """
                                            {
                                                "Ksp2InstallPath": "ksp2 install patch",
                                                "ReleaseChannel": "channel",
                                                "LastInstalledVersion": null,
                                                "Feeds": []
                                            }
                                            """;

    private LauncherConfig simpleLauncherConfig => new()
    {
        Ksp2InstallPath = "ksp2 install patch",
        ReleaseChannel = "channel",
        LastInstalledVersion = null,
        Feeds = [],
        StoragePath = "/appdata/Ksp2Redux/redux-launcher-config.json"
    };

    // Same content as simpleLauncherConfigJson but already migrated to the multi-install schema.
    private string simpleLauncherConfigJsonMigrated => """
                                                       {
                                                           "Ksp2InstallPath": "",
                                                           "ReleaseChannel": "channel",
                                                           "LastInstalledVersion": null,
                                                           "Feeds": [],
                                                           "Ksp2Installs": [
                                                               { "Id": "11111111-1111-1111-1111-111111111111", "Name": "patch", "ExePath": "ksp2 install patch", "ReleaseChannel": "channel", "LastInstalledVersion": null }
                                                           ],
                                                           "ActiveKsp2InstallId": "11111111-1111-1111-1111-111111111111"
                                                       }
                                                       """;
    
    private string emptyLauncherConfigJson = """
                                            {
                                                "Ksp2InstallPath": "",
                                                "ReleaseChannel": "",
                                                "LastInstalledVersion": null,
                                                "Feeds": []
                                            }
                                            """;

    private LauncherConfig emptyLauncherConfig => new()
    {
        Ksp2InstallPath = "",
        ReleaseChannel = "",
        LastInstalledVersion = null,
        Feeds = [],
        StoragePath = "/appdata/Ksp2Redux/redux-launcher-config.json"
    };
    
    // ctor
    [Test]
    [Description("""
                 If for some reasons there is no path to LocalApplicationData:
                    - The ctor should throw an exception
                    - It shouldn't try to access/create the directory
                    - It shouldn't try to access the file
                    - It shouldn't try to create a new file
                 Here the program doesn't find a file similar to the config
                 """)]
    public void Constructor_NoAppDataFolderNoSimilarFile_ThrowsExceptionAndNoFileSystemAction()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("");
        path.Setup(p => p.Combine(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("incorrect combined path");
        file.Setup(f => f.ReadAllText("incorrect combined path"))
            .Throws<FileNotFoundException>();
        path.Setup(p => p.GetDirectoryName(It.IsAny<string>()))
            .Returns("incorrect directory");
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);
        
        // Act Assert
        Assert.That(
            () => new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object),
            Throws.Exception);
        directory.Verify(d => d.CreateDirectory("incorrect combined path"), Times.Never,
            "Tried to create a directory for the config when appdata didn't exist");
        file.Verify(d => d.ReadAllText("incorrect combined path"), Times.Never, 
            "Tried to read a file when appdata didn't exist");
        file.Verify(d => d.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Never, 
            "Tried to create a file when appdata didn't exist");
    }
    
    [Test]
    [Description("""
                 If for some reasons there is no path to LocalApplicationData:
                    - The ctor should throw an exception
                    - It shouldn't try to access/create the directory
                    - It shouldn't try to access the file
                    - It shouldn't try to create a new file
                 Here the program finds a file similar to the config, it should read it anyway
                 """)]
    public void Constructor_NoAppDataFolderWithSimilarFile_ThrowsExceptionAndNoFileSystemAction()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("");
        path.Setup(p => p.Combine(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("incorrect combined path");
        file.Setup(f => f.ReadAllText("incorrect combined path"))
            .Returns(emptyLauncherConfigJson); // Case where it reads an incorrect file of the same name
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);
        
        // Act Assert
        Assert.That(
            () => new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object),
            Throws.Exception);
        directory.Verify(d => d.CreateDirectory("incorrect combined path"), Times.Never,
            "Tried to create a directory for the config when appdata didn't exist");
        file.Verify(d => d.ReadAllText("incorrect combined path"), Times.Never, 
            "Tried to read a file when appdata didn't exist");
    }

    [Test]
    public void Constructor_FailToReadAllText_WritesNewFile()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux"))
            .Returns("/appdata/Ksp2Redux");
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux/redux-launcher-config.json");
        file.Setup(f => f.ReadAllText("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Throws<IOException>();
        path.Setup(p => p.GetDirectoryName("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux");
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);
        
        // Act
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);
        
        // Assert
        file.Verify(f => f.WriteAllText("/appdata/Ksp2Redux/redux-launcher-config.json", It.IsAny<string>()), Times.Once);
        Assert.That(launcherConfigService.Config, Is.Not.Null);
    }

    [Test]
    public void Constructor_FailToDeserialize_WritesNewFile()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux"))
            .Returns("/appdata/Ksp2Redux");
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux/redux-launcher-config.json");
        file.Setup(f => f.ReadAllText("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("incorrect JSON");
        path.Setup(p => p.GetDirectoryName("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux");
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);
        
        // Act
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);
        
        // Assert
        file.Verify(f => f.WriteAllText("/appdata/Ksp2Redux/redux-launcher-config.json", It.IsAny<string>()), Times.Once);
        Assert.That(launcherConfigService.Config, Is.Not.Null);
    }

    [Test]
    public void Constructor_NullConfig_WritesNewFile()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux"))
            .Returns("/appdata/Ksp2Redux");
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux/redux-launcher-config.json");
        file.Setup(f => f.ReadAllText("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("null");
        path.Setup(p => p.GetDirectoryName("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux");
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);
        
        // Act
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);
        
        // Assert
        file.Verify(f => f.WriteAllText("/appdata/Ksp2Redux/redux-launcher-config.json", It.IsAny<string>()), Times.Once);
        Assert.That(launcherConfigService.Config, Is.Not.Null);
    }

    [Test]
    public void Constructor_CorrectDeserialize_MigratesLegacySingleInstall()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();

        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux"))
            .Returns("/appdata/Ksp2Redux");
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux/redux-launcher-config.json");
        file.Setup(f => f.ReadAllText("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns(simpleLauncherConfigJson);
        path.Setup(p => p.GetDirectoryName("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux");

        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        // Act
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);

        // Assert: legacy single-install was migrated to the new schema.
        var cfg = launcherConfigService.Config;
        Assert.Multiple(() =>
        {
            Assert.That(cfg.Ksp2InstallPath, Is.Empty, "Legacy install path should be cleared after migration.");
            Assert.That(cfg.LastInstalledVersion, Is.Null, "Legacy LastInstalledVersion should be cleared after migration.");
            Assert.That(cfg.Ksp2Installs, Has.Count.EqualTo(1));
            Assert.That(cfg.Ksp2Installs[0].ExePath, Is.EqualTo("ksp2 install patch"));
            Assert.That(cfg.Ksp2Installs[0].ReleaseChannel, Is.EqualTo("channel"));
            Assert.That(cfg.ActiveKsp2InstallId, Is.EqualTo(cfg.Ksp2Installs[0].Id));
        });
        // Migration must persist immediately so the file isn't re-migrated next launch.
        file.Verify(f => f.WriteAllText("/appdata/Ksp2Redux/redux-launcher-config.json", It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void Constructor_AlreadyMigratedConfig_DoesNotRunMigration()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();

        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux"))
            .Returns("/appdata/Ksp2Redux");
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux/redux-launcher-config.json");
        file.Setup(f => f.ReadAllText("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns(simpleLauncherConfigJsonMigrated);
        path.Setup(p => p.GetDirectoryName("/appdata/Ksp2Redux/redux-launcher-config.json"))
            .Returns("/appdata/Ksp2Redux");

        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        // Act
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);

        // Assert
        var cfg = launcherConfigService.Config;
        Assert.That(cfg.Ksp2Installs, Has.Count.EqualTo(1));
        Assert.That(cfg.ActiveKsp2InstallId, Is.EqualTo(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        // Already migrated => no migration save expected.
        file.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
    
    // Save
    [Test]
    public void Save_NullStoragePathDirectoryName_ReportsFailureInsteadOfThrowing()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        Mock<ILogService> log = new();

        // Pass constructor quickly without issues
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux")).Returns("/appdata/Ksp2Redux");
        directory.Setup(d => d.CreateDirectory("/appdata/Ksp2Redux"));
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("configFilePath");
        file.Setup(f => f.ReadAllText("configFilePath")).Returns(simpleLauncherConfigJson);
        // simpleLauncherConfigJson is legacy-schema, so construction triggers a migration Save() too -
        // give that one a valid directory so only the explicit Save() below hits the failure path.
        path.Setup(p => p.GetDirectoryName("configFilePath"))
            .Returns("/appdata/Ksp2Redux");

        // Test method
        path.Setup(p => p.GetDirectoryName("storage path"))
            .Returns((string?)null);

        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        LauncherConfigService launcherConfigService = null!;
        try
        {
            launcherConfigService = new(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, log.Object);
        }
        catch(Exception e)
        {
            Assert.Inconclusive($"Couldn't finish arrange because of exception: {e}");
        }

        launcherConfigService.Config = new()
        {
            StoragePath = "storage path"
        };

        // Act Assert - a failed save must never throw back into a UI data-binding callback; it should
        // be logged instead so the app stays up (see the "stop the crashes" hardening pass).
        Assert.That(() => launcherConfigService.Save(), Throws.Nothing);
        log.Verify(l => l.Error(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        directory.Verify(d => d.CreateDirectory(It.IsNotIn("/appdata/Ksp2Redux")), Times.Never,
            "Tried creating a directory when IPath.GetDirectoryName returned null");
        file.Verify(f => f.WriteAllText("storage path", It.IsAny<string>()), Times.Never,
            "Tried writing to storage path when IPath.GetDirectoryName returned null");
    }
    
    [Test]
    public void Save_Correct_Success()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();
        Mock<IDirectoryInfo> createdDirectoryInfo = new();

        // Pass constructor quickly without issues
        environmentProvider.Setup(ep => ep.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Returns("/appdata");
        path.Setup(p => p.Combine("/appdata", "Ksp2Redux")).Returns("/appdata/Ksp2Redux");
        directory.Setup(d => d.CreateDirectory("/appdata/Ksp2Redux"));
        path.Setup(p => p.Combine("/appdata/Ksp2Redux", "redux-launcher-config.json"))
            .Returns("configFilePath");
        file.Setup(f => f.ReadAllText("configFilePath")).Returns(simpleLauncherConfigJson);
        
        // Test method
        path.Setup(p => p.GetDirectoryName("storage path"))
            .Returns("storage directory");
        directory.Setup(d => d.CreateDirectory(It.IsNotIn("/appdata/Ksp2Redux")))
            .Returns(createdDirectoryInfo.Object);
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        LauncherConfigService launcherConfigService = null!;
        try
        {
            launcherConfigService = new(fileSystem.Object, environmentProvider.Object, new Mock<IMessageBoxService>().Object, new Mock<ILogService>().Object);
        }
        catch(Exception e)
        {
            Assert.Inconclusive($"Couldn't finish arrange because of exception: {e}");
        }

        launcherConfigService.Config = emptyLauncherConfig;
        launcherConfigService.Config.StoragePath = "storage path";
        
        // Act
        launcherConfigService.Save();
        
        // Assert
        file.Verify(f => f.WriteAllText("storage path", It.IsAny<string>()), Times.Once);
    }
}
