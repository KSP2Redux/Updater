using System.Globalization;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.Tests.Localization;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class InstallLogLanguageTest
{
    private CultureInfo _uiCulture = null!;
    private CultureInfo? _defaultUiCulture;

    [SetUp]
    public void RememberCultures()
    {
        _uiCulture = CultureInfo.CurrentUICulture;
        _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    }

    [TearDown]
    public void RestoreCultures()
    {
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    [AvaloniaTest]
    public void LogAndLogError_WriteEnglishToTheFileLogAndTheChosenLanguageToTheInstallLog()
    {
        // Arrange
        var localization = new LocalizationService(new FakeStringSource()
                .Add("en", "Home.Log.Installed", "Installed.").Add("de", "Home.Log.Installed", "Installiert.")
                .Add("en", "Home.Log.UpdateError", "Update failed: {0}").Add("de", "Home.Log.UpdateError", "Update fehlgeschlagen: {0}"),
            CultureInfo.GetCultureInfo("de-DE"));
        localization.SetLanguage(null);
        var log = new Mock<ILogService>();
        var installs = new Mock<IKsp2InstallService>();
        installs.SetupGet(service => service.Entries).Returns([]);
        var viewModel = new HomeTabViewModel(installs.Object, Mock.Of<ILauncherConfigService>(), Mock.Of<IReleasesFeedService>(),
            Mock.Of<IInstallPlanService>(), Mock.Of<IUpdateService>(), Mock.Of<IOperatingSystemService>(), Mock.Of<IMessageBoxService>(),
            Mock.Of<IEnvironmentProvider>(), new MockFileSystem(), log.Object, Mock.Of<IWineRuntimeService>(),
            Mock.Of<IGameProcessService>(), localization);
        var failure = new LogMessage("disk full", "Platte voll").ToException(text => new IOException(text));

        // Act
        viewModel.Log(localization.Message("Home.Log.Installed"));
        viewModel.LogError(localization.Message("Home.Log.UpdateError", failure.ToLogMessage()), failure);
        Dispatcher.UIThread.RunJobs();

        // Assert
        log.Verify(l => l.Info("Installed.", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        log.Verify(l => l.Error("Update failed: disk full", failure, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.That(viewModel.InstallLogText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            Is.EqualTo(new[] { "Installiert.", "Update fehlgeschlagen: Platte voll" }));
    }
}
