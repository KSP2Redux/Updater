using System.IO.Abstractions;
using System.Text;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public class SourceStockBootPreservationTest
{
    [TestCase("internal", true)]
    [TestCase("mr", true)]
    [TestCase("next", true)]
    [TestCase("next-mr", true)]
    [TestCase("beta", false)]
    public void SelectingARecipeChannel_PreservesOriginalBootBytesBeforeAnyFeedIsLoaded(string channel, bool preserve)
    {
        var fs = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Windows));
        const string root = @"C:\FixtureGame";
        string exe = fs.Path.Combine(root, "KSP2_x64.exe");
        string boot = fs.Path.Combine(root, "KSP2_x64_Data", "boot.config");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(boot)!);
        fs.File.WriteAllBytes(exe, [1]);
        byte[] original = Encoding.UTF8.GetBytes("wait-for-native-debugger=0\n");
        fs.File.WriteAllBytes(boot, original);
        var entry = new Ksp2InstallEntry { ExePath = exe, ReleaseChannel = channel };
        var config = new LauncherConfig { ActiveKsp2InstallId = entry.Id };
        config.Ksp2Installs.Add(entry);
        var settings = new Mock<ILauncherConfigService>();
        settings.SetupGet(value => value.Config).Returns(config);
        var service = new Ksp2InstallService(settings.Object, fs, Mock.Of<IModuleDefinitionService>(),
            Mock.Of<ILogService>(), new InstallationStateService(fs, new ZipFileService(fs)));
        service.TryLoadKsp2Install();
        service.ApplyActiveInstallBootConfig();
        if (preserve)
            Assert.That(fs.File.ReadAllBytes(boot), Is.EqualTo(original));
        else
            Assert.That(fs.File.ReadAllText(boot), Does.Contain("gfx-enable-gfx-jobs=1"));
    }
}