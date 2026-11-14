using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class UninstallCommandTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";

    private static CliCommandHarness WithReduxGame(bool withSnapshot)
    {
        var harness = new CliCommandHarness();
        harness.Installs.AddInstall(harness.CreateGame(GAME), "Testing");
        harness.FileSystem.Directory.CreateDirectory($@"{GAME}\Redux\Assets");
        if (withSnapshot)
        {
            harness.FileSystem.File.WriteAllBytes($@"{GAME}\uninstall.zip", [0x00]);
        }

        return harness;
    }

    [Test]
    public async Task Uninstall_ReduxWithoutSnapshot_FailsWithoutTouchingTheGame()
    {
        var harness = WithReduxGame(withSnapshot: false);

        var exit = await new UninstallCommand().RunWithContextAsync(harness.Context, new UninstallSettings { AssumeYes = true });

        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.Results.ToString(), Does.Contain("uninstall.zip"));
    }

    [Test]
    public async Task Uninstall_ReduxWithSnapshot_RestoresStock()
    {
        var harness = WithReduxGame(withSnapshot: true);

        var exit = await new UninstallCommand().RunWithContextAsync(harness.Context, new UninstallSettings { AssumeYes = true });

        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("removed").GetBoolean(), Is.True);
    }
}
