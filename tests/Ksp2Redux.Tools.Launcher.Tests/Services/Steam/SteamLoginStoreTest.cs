using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Steam;

public class SteamLoginStoreTest
{
    private const string STORAGE = @"C:\Users\player\AppData\Local\Ksp2Redux";

    private static SteamLoginStore Build(MockFileSystem fileSystem)
    {
        var config = new Mock<ILauncherConfigService>();
        config.Setup(c => c.GetLocalStorageDirectory()).Returns(STORAGE);
        var operatingSystem = new Mock<IOperatingSystemService>();
        return new SteamLoginStore(fileSystem, config.Object, operatingSystem.Object, new Mock<ILogService>().Object);
    }

    [Test]
    public void Save_ThenLoad_ReturnsTheSameLogin()
    {
        // Arrange
        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var store = Build(fileSystem);
        var login = new SavedSteamLogin("kerbal", "refresh-token", "guard-data");

        // Act
        store.Save(login);

        // Assert
        Assert.That(store.Load(), Is.EqualTo(login));
    }

    [Test]
    public void Clear_RemovesTheSavedToken()
    {
        // Arrange
        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var store = Build(fileSystem);
        store.Save(new SavedSteamLogin("kerbal", "refresh-token", null));

        // Act
        store.Clear();

        // Assert
        Assert.That(store.Load(), Is.Null);
        Assert.That(fileSystem.File.Exists(fileSystem.Path.Combine(STORAGE, SteamLoginStore.FILE_NAME)), Is.False);
    }

    [Test]
    public void Load_CorruptFile_IsTreatedAsSignedOut()
    {
        // Arrange
        var fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fileSystem.Directory.CreateDirectory(STORAGE);
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(STORAGE, SteamLoginStore.FILE_NAME), "{ not json");

        // Act
        var login = Build(fileSystem).Load();

        // Assert
        Assert.That(login, Is.Null);
    }

    [SetCulture("en-US")]
    [TestCase(512L, "512 B")]
    [TestCase(1536L, "1.5 KB")]
    [TestCase(33337259792L, "31.0 GB")]
    public void FormatBytes_UsesBinaryUnits(long bytes, string expected)
    {
        Assert.That(SteamDownloadViewModel.FormatBytes(bytes), Is.EqualTo(expected));
    }
}
