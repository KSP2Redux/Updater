using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Infrastructure;

public class IsolatedLauncherStorageTest
{
    [Test]
    public void ExplicitProcessStorage_KeepsConfigAndLogsOutsideTheUserProfile()
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        var environment = new Mock<IEnvironmentProvider>();
        environment.Setup(value => value.GetEnvironmentVariable("KSP2REDUX_LAUNCHER_STORAGE")).Returns(@"C:\OwnedTest\Launcher");
        environment.Setup(value => value.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).Returns(@"C:\UserProfile");
        Assert.That(LocalStoragePaths.GetLocalStorageDirectory(fs, environment.Object), Is.EqualTo(@"C:\OwnedTest\Launcher"));
        Assert.That(LocalStoragePaths.GetLogsDirectory(fs, environment.Object), Is.EqualTo(@"C:\OwnedTest\Launcher\logs"));
    }

    [Test]
    public void RelativeProcessStorage_IsRejectedBeforeAnyDirectoryIsCreated()
    {
        var fs = new MockFileSystem();
        var environment = new Mock<IEnvironmentProvider>();
        environment.Setup(value => value.GetEnvironmentVariable("KSP2REDUX_LAUNCHER_STORAGE")).Returns("relative-location");
        Assert.That(() => LocalStoragePaths.GetLocalStorageDirectory(fs, environment.Object), Throws.TypeOf<InvalidOperationException>());
    }
}