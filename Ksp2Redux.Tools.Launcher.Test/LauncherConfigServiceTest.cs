using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Test;

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
    public void Constructor_NoAppData_ThrowsExceptionAndNoFileSystemAction()
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
        Assert.Throws<Exception>(() => new LauncherConfigService(fileSystem.Object, environmentProvider.Object));
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
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object);
        
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
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object);
        
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
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object);
        
        // Assert
        file.Verify(f => f.WriteAllText("/appdata/Ksp2Redux/redux-launcher-config.json", It.IsAny<string>()), Times.Once);
        Assert.That(launcherConfigService.Config, Is.Not.Null);
    }

    [Test]
    public void Constructor_CorrectDeserialize_CorrectConfig()
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
        LauncherConfigService launcherConfigService = new LauncherConfigService(fileSystem.Object, environmentProvider.Object);
        
        // Assert
        Assert.That(launcherConfigService.Config, Is.EqualTo(simpleLauncherConfig).UsingPropertiesComparer());
    }
    
    // Save
    [Test]
    public void Save_NullStoragePathDirectoryName_ThrowsException()
    {
        // Arrange
        Mock<IEnvironmentProvider> environmentProvider  = new();
        Mock<IPath> path = new();
        Mock<IDirectory> directory = new();
        Mock<IFile> file = new();
        Mock<IFileSystem> fileSystem = new();

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
            .Returns((string?)null);
        
        fileSystem.SetupGet(fs => fs.Path).Returns(path.Object);
        fileSystem.SetupGet(fs => fs.Directory).Returns(directory.Object);
        fileSystem.SetupGet(fs => fs.File).Returns(file.Object);

        LauncherConfigService launcherConfigService = null!;
        try
        {
            launcherConfigService = new(fileSystem.Object, environmentProvider.Object);
        }
        catch(Exception e)
        {
            Assert.Inconclusive($"Couldn't finish arrange because of exception: {e}");
        }

        launcherConfigService.Config = new()
        {
            StoragePath = "storage path"
        };
        
        // Act Assert
        Assert.Throws<Exception>(() => launcherConfigService.Save());
        directory.Verify(d => d.CreateDirectory(It.IsNotIn("/appdata/Ksp2Redux")), Times.Never, 
            "Tried creating a directory when IPath.GetDirectoryName returned null");
        file.Verify(f => f.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "Tried creating a directory when IPath.GetDirectoryName returned null");
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
            launcherConfigService = new(fileSystem.Object, environmentProvider.Object);
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