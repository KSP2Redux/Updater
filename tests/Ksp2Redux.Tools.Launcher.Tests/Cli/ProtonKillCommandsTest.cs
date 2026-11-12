using System.Text;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class ProtonKillCommandsTest
{
    private static CliCommandHarness WithProc(int owner)
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Linux));
        var harness = new CliCommandHarness(fileSystem);
        harness.OperatingSystem.Setup(system => system.IsLinux()).Returns(true);
        fileSystem.Directory.CreateDirectory("/proc/self");
        fileSystem.File.WriteAllText("/proc/self/status", "Uid:\t1000\t1000\t1000\t1000\n");
        string directory = "/proc/2147483647";
        fileSystem.Directory.CreateDirectory(directory);
        fileSystem.File.WriteAllText(directory + "/status", $"Name:\tUnity Main Thre\nUid:\t{owner}\t{owner}\t{owner}\t{owner}\n");
        fileSystem.File.CreateSymbolicLink(directory + "/exe", "/runtime/files/bin/wine64-preloader");
        fileSystem.File.WriteAllBytes(directory + "/cmdline", Encoding.UTF8.GetBytes("X:\\Games\\KSP2\\KSP2_x64.exe\0-logFile\0Player.log\0"));
        return harness;
    }

    [Test]
    public async Task LinuxKillFindsTheRenamedWineGameAndRequiresConfirmation()
    {
        var harness = WithProc(1000);

        int exitCode = await new KillCommand().RunWithContextAsync(harness.Context, new KillSettings());

        Assert.That(exitCode, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain("Refusing to stop"));
        harness.Wine.Verify(runtime => runtime.Detect(), Times.Never);
    }

    [Test]
    public async Task LinuxKillDoesNotSelectAnotherAccountsWineGame()
    {
        var harness = WithProc(2000);

        int exitCode = await new KillCommand().RunWithContextAsync(harness.Context, new KillSettings { AssumeYes = true });

        Assert.That(exitCode, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("killed").GetInt32(), Is.Zero);
        harness.Wine.Verify(runtime => runtime.Detect(), Times.Never);
    }
}
