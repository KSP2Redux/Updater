using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Localization;

public class LauncherConfigLanguageTest
{
    private const string ConfigPath = @"C:\AppDataLocal\Ksp2Redux\redux-launcher-config.json";

    private MockFileSystem _fileSystem = null!;
    private MockEnvironmentProvider _environment = null!;

    [SetUp]
    public void CreateFileSystem()
    {
        _fileSystem = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        _environment = new MockEnvironmentProvider();
        _environment.SetFolderPath(Environment.SpecialFolder.LocalApplicationData, @"C:\AppDataLocal");
    }

    private LauncherConfigService CreateService() =>
        new(_fileSystem, _environment, Mock.Of<IMessageBoxService>(), Mock.Of<ILogService>());

    [Test]
    public void Load_ConfigFromBeforeTheLanguageSetting_FollowsTheOperatingSystem()
    {
        _fileSystem.Directory.CreateDirectory(@"C:\AppDataLocal\Ksp2Redux");
        _fileSystem.File.WriteAllText(ConfigPath, """{ "Ksp2InstallPath": "", "ReleaseChannel": "beta", "Feeds": [] }""");

        var config = CreateService().Config;

        Assert.That(config.Language, Is.Empty);
        Assert.That(config.ReleaseChannel, Is.EqualTo("beta"));
    }

    [Test]
    public void Save_ThenLoad_KeepsTheLanguage()
    {
        var service = CreateService();
        service.Config.Language = "pt-BR";
        service.Save();

        Assert.That(_fileSystem.File.ReadAllText(ConfigPath), Does.Contain("\"Language\": \"pt-BR\""));
        Assert.That(CreateService().Config.Language, Is.EqualTo("pt-BR"));
    }
}
