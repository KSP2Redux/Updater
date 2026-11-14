using Ksp2Redux.Tools.Cli.Infrastructure;
using System.Diagnostics;
using Testably.Abstractions;

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

    [Test]
    public async Task WithOutputTo_ArgumentString_PreservesQuotedArgumentsWithoutEvaluatingShellText()
    {
        var fileSystem = new RealFileSystem();
        if (!fileSystem.File.Exists("/bin/sh"))
            Assert.Ignore("The Unix process-output wrapper requires /bin/sh.");

        string directory = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), $"Redux output \"quote\" {Guid.NewGuid():N}");
        fileSystem.Directory.CreateDirectory(directory);
        try
        {
            string program = fileSystem.Path.Combine(directory, "shell \"quoted\"");
            fileSystem.File.CreateSymbolicLink(program, "/bin/sh");
            string output = fileSystem.Path.Combine(directory, "game output \"quoted\".log");
            var launch = new ProcessStartInfo(program)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                Arguments = "-c \"printf '%s\\n' \\\"$@\\\"\" argv-zero " + """
                    "" "two words" "a\"quote" "tail\\" "$(printf injected > marker)" "`printf injected`" "--flag" "unicode ☃"
                    """
            };

            using var direct = Process.Start(launch)!;
            string expected = await direct.StandardOutput.ReadToEndAsync();
            await direct.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(direct.ExitCode, Is.Zero);

            using var wrapped = Process.Start(CliWineProcesses.WithOutputTo(launch, output))!;
            await wrapped.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(wrapped.ExitCode, Is.Zero);
            Assert.That(fileSystem.File.ReadAllText(output), Is.EqualTo(expected));
            Assert.That(fileSystem.File.Exists(fileSystem.Path.Combine(directory, "marker")), Is.False);
        }
        finally
        {
            fileSystem.Directory.Delete(directory, recursive: true);
        }
    }
}
