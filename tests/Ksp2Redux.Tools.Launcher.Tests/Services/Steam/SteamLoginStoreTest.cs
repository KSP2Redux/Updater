using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Ksp2Redux.Tools.Launcher.ViewModels.Steam;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Steam;

public class SteamLoginStoreTest
{
    private const string STORAGE = @"C:\Users\player\AppData\Local\Ksp2Redux";
    private static readonly SavedSteamLogin LOGIN = new("kerbal", "refresh-token", "guard-data");

    private sealed class FakeVault : ISteamLoginVault
    {
        public string? Stored { get; set; }

        public bool Fails { get; init; }

        public string Name => "fake vault";

        public string? Read() => Fails ? throw new IOException("locked") : Stored;

        public void Write(string payload)
        {
            if (Fails) throw new IOException("locked");
            Stored = payload;
        }

        public void Delete() => Stored = null;
    }

    private static MockFileSystem NewFileSystem() => new(o => o.SimulatingOperatingSystem(SimulationMode.Windows));

    private static SteamLoginStore Build(MockFileSystem fileSystem, ISteamLoginVault? vault)
    {
        var config = new Mock<ILauncherConfigService>();
        config.Setup(c => c.GetLocalStorageDirectory()).Returns(STORAGE);
        return new SteamLoginStore(fileSystem, config.Object, new Mock<IOperatingSystemService>().Object, new Mock<ILogService>().Object, vault);
    }

    private static string PlainFile(MockFileSystem fileSystem) => fileSystem.Path.Combine(STORAGE, SteamLoginStore.FILE_NAME);

    [Test]
    public void Save_WithAVault_KeepsTheTokenOutOfThePlainFile()
    {
        // Arrange
        var fileSystem = NewFileSystem();
        var vault = new FakeVault();

        // Act
        Build(fileSystem, vault).Save(LOGIN);

        // Assert
        Assert.That(vault.Stored, Does.Contain("refresh-token"));
        Assert.That(fileSystem.File.Exists(PlainFile(fileSystem)), Is.False);
        Assert.That(Build(fileSystem, vault).Load(), Is.EqualTo(LOGIN));
    }

    // Logins saved before the secret store existed are moved into it on first read.
    [Test]
    public void Load_PlainFileFromAnOlderVersion_IsMovedIntoTheVault()
    {
        // Arrange
        var fileSystem = NewFileSystem();
        Build(fileSystem, null).Save(LOGIN);
        var vault = new FakeVault();

        // Act
        var loaded = Build(fileSystem, vault).Load();

        // Assert
        Assert.That(loaded, Is.EqualTo(LOGIN));
        Assert.That(vault.Stored, Does.Contain("refresh-token"));
        Assert.That(fileSystem.File.Exists(PlainFile(fileSystem)), Is.False);
    }

    [Test]
    public void Save_VaultFails_FallsBackToTheOwnerOnlyFile()
    {
        // Arrange
        var fileSystem = NewFileSystem();
        var store = Build(fileSystem, new FakeVault { Fails = true });

        // Act
        store.Save(LOGIN);

        // Assert
        Assert.That(fileSystem.File.Exists(PlainFile(fileSystem)), Is.True);
        Assert.That(store.Load(), Is.EqualTo(LOGIN));
    }

    [Test]
    public void Clear_RemovesTheLoginFromTheVaultAndTheFile()
    {
        // Arrange
        var fileSystem = NewFileSystem();
        Build(fileSystem, null).Save(LOGIN);
        var vault = new FakeVault { Stored = "{}" };

        // Act
        Build(fileSystem, vault).Clear();

        // Assert
        Assert.That(vault.Stored, Is.Null);
        Assert.That(fileSystem.File.Exists(PlainFile(fileSystem)), Is.False);
    }

    [Test]
    public void Load_CorruptFile_IsTreatedAsSignedOut()
    {
        // Arrange
        var fileSystem = NewFileSystem();
        fileSystem.Directory.CreateDirectory(STORAGE);
        fileSystem.File.WriteAllText(PlainFile(fileSystem), "{ not json");

        // Act
        var login = Build(fileSystem, null).Load();

        // Assert
        Assert.That(login, Is.Null);
    }

    [Test]
    [Platform("Win")]
    public void DpapiVault_RoundTrip_StoresNoReadableToken()
    {
        // Arrange
        var fileSystem = NewFileSystem();
        fileSystem.Directory.CreateDirectory(STORAGE);
        var path = fileSystem.Path.Combine(STORAGE, SteamLoginStore.PROTECTED_FILE_NAME);
#pragma warning disable CA1416
        var vault = new DpapiSteamLoginVault(fileSystem, path);

        // Act
        vault.Write("refresh-token");

        // Assert
        Assert.That(System.Text.Encoding.UTF8.GetString(fileSystem.File.ReadAllBytes(path)), Does.Not.Contain("refresh-token"));
        Assert.That(vault.Read(), Is.EqualTo("refresh-token"));
#pragma warning restore CA1416
    }

    [Test]
    [Platform("MacOsX")]
    [Explicit("Writes to the login keychain")]
    public void KeychainVault_RoundTrip_ReadsBackAndDeletes()
    {
        // Arrange
#pragma warning disable CA1416
        var vault = new KeychainSteamLoginVault("KSP2 Redux test " + Guid.NewGuid());
        const string payload = "{\"AccountName\":\"kerbal\",\"RefreshToken\":\"a.b-c_d\"}";

        try
        {
            // Act
            vault.Write(payload);

            // Assert
            Assert.That(vault.Read(), Is.EqualTo(payload));
        }
        finally
        {
            vault.Delete();
        }

        Assert.That(vault.Read(), Is.Null);
#pragma warning restore CA1416
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
