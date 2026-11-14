using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Infrastructure;

public class LauncherInstanceTest
{
    [Test]
    public void IsRunning_AfterMarkRunning_IsTrue()
    {
        LauncherInstance.MarkRunning();

        Assert.That(LauncherInstance.IsRunning(), Is.True);
    }
}
