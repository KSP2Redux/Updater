using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

// Under Wine, .NET sees every process as "wine".
public class CliWineProcessesTest
{
    [Test]
    public void ParseGame_PsListing_FindsTheGameByItsCommandName()
    {
        // Arrange
        const string listing = """
              2934 /Users/player/Movies/Kerbal Space Program 2/KSP2_x64.exe
              2941 /Applications/KSP2 Redux.app/Contents/Resources/wine-runtime/wine/bin/wineserver
              2949 C:\windows\system32\winedevice.exe
              3001 C:\Games\KSP2\KSP2_x64.exe
              3100 /bin/zsh
            """;

        // Act
        var pids = CliWineProcesses.ParseGame(listing);

        // Assert
        Assert.That(pids, Is.EqualTo(new[] { 2934, 3001 }));
    }

    [Test]
    public void ParseGame_SimilarlyNamedProgram_IsNotTheGame()
    {
        // Act
        var pids = CliWineProcesses.ParseGame("  10 /tmp/not-KSP2_x64.exe\n  11 /tmp/KSP2_x64.exe.bak\n  wine\n");

        // Assert
        Assert.That(pids, Is.Empty);
    }

    [Test]
    public void WithOutputTo_Launch_KeepsTheProgramArgumentsAndEnvironment()
    {
        // Arrange
        System.Diagnostics.ProcessStartInfo launch = new("/runtime/wine/bin/wine") { WorkingDirectory = "/game" };
        launch.ArgumentList.Add("/game/KSP2_x64.exe");
        launch.ArgumentList.Add("-popupwindow");
        launch.Environment["WINEPREFIX"] = "/prefix";

        // Act
        var wrapped = CliWineProcesses.WithOutputTo(launch, "/logs/game-output.log");

        // Assert
        Assert.That(wrapped.FileName, Is.EqualTo("/bin/sh"));
        Assert.That(wrapped.ArgumentList.Skip(2), Is.EqualTo(new[] { "/runtime/wine/bin/wine", "/game/KSP2_x64.exe", "-popupwindow" }));
        Assert.That(wrapped.ArgumentList[1], Does.StartWith("exec "));
        Assert.That(wrapped.WorkingDirectory, Is.EqualTo("/game"));
        Assert.That(wrapped.Environment["WINEPREFIX"], Is.EqualTo("/prefix"));
        Assert.That(wrapped.Environment["REDUX_GAME_OUTPUT"], Is.EqualTo("/logs/game-output.log"));
    }
}
