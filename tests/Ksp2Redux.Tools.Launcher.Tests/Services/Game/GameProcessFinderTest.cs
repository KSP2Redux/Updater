using System.Text;
using Ksp2Redux.Tools.Launcher.Services.Game;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Game;

public class GameProcessFinderTest
{
    private static MockFileSystem NewProc()
    {
        var fileSystem = new MockFileSystem(options => options.SimulatingOperatingSystem(SimulationMode.Linux));
        fileSystem.Directory.CreateDirectory("/proc/self");
        fileSystem.File.WriteAllText("/proc/self/status", "Name:\tlauncher\nUid:\t1000\t1000\t1000\t1000\n");
        return fileSystem;
    }

    private static void AddProcess(MockFileSystem fileSystem, int pid, string program,
        string loader = "/runtime/files/bin/wine64-preloader", int owner = 1000)
    {
        string directory = $"/proc/{pid}";
        fileSystem.Directory.CreateDirectory(directory);
        fileSystem.File.WriteAllText(directory + "/status", $"Name:\tUnity Main Thre\nUid:\t{owner}\t{owner}\t{owner}\t{owner}\n");
        fileSystem.File.WriteAllText(directory + "/comm", "Unity Main Thre\n");
        fileSystem.File.WriteAllBytes(directory + "/cmdline", Encoding.UTF8.GetBytes(program + "\0-logFile\0X:\\Logs\\Player.log\0"));
        fileSystem.File.CreateSymbolicLink(directory + "/exe", loader);
    }

    [Test]
    public void UnityRenamedMainThreadIsFoundThroughItsWindowsArgvZero()
    {
        var fileSystem = NewProc();
        AddProcess(fileSystem, 214186, @"X:\Games\KSP2-working\KSP2_x64.exe");

        Assert.That(GameProcessFinder.FindUnderProton(fileSystem), Is.EqualTo(new[] { 214186 }));
    }

    [TestCase("wine")]
    [TestCase("wine64")]
    [TestCase("wine-preloader")]
    [TestCase("wine64-preloader")]
    [TestCase("wine64-preloader (deleted)")]
    public void WineLoaderVariantsAndReplacedRuntimeBinariesRemainRecognizable(string loader)
    {
        var fileSystem = NewProc();
        AddProcess(fileSystem, 123, "/home/player/Games/KSP2/KSP2_x64.exe", "/runtime/files/bin/" + loader);

        Assert.That(GameProcessFinder.FindUnderProton(fileSystem), Is.EqualTo(new[] { 123 }));
    }

    [Test]
    public void GameNamesInLaterArgumentsDoNotMakeShellsOrWineHelpersLookLikeTheGame()
    {
        var fileSystem = NewProc();
        AddProcess(fileSystem, 10, "python3", "/usr/bin/python3");
        AddProcess(fileSystem, 11, @"C:\windows\system32\cmd.exe");
        AddProcess(fileSystem, 12, @"C:\Games\KSP2\KSP2_x64.exe.bak");
        foreach (int pid in new[] { 10, 11, 12 })
        {
            fileSystem.File.AppendAllText($"/proc/{pid}/cmdline", "C:\\Games\\KSP2\\KSP2_x64.exe\0");
        }

        Assert.That(GameProcessFinder.FindUnderProton(fileSystem), Is.Empty);
    }

    [Test]
    public void AnotherUsersGameAndNativeProgramsWithSpoofedArgvZeroAreExcluded()
    {
        var fileSystem = NewProc();
        AddProcess(fileSystem, 10, @"C:\Games\KSP2\KSP2_x64.exe", owner: 2000);
        AddProcess(fileSystem, 11, @"C:\Games\KSP2\KSP2_x64.exe", "/usr/bin/python3");
        AddProcess(fileSystem, 12, @"C:\Games\KSP2\KSP2_x64.exe", "/usr/bin/wineserver");

        Assert.That(GameProcessFinder.FindUnderProton(fileSystem), Is.Empty);
    }

    [Test]
    public void ExitingMalformedOrMissingProcessesDoNotHideAnotherRunningGame()
    {
        var fileSystem = NewProc();
        fileSystem.Directory.CreateDirectory("/proc/10");
        AddProcess(fileSystem, 11, @"C:\Games\KSP2\KSP2_x64.exe");
        fileSystem.File.WriteAllBytes("/proc/11/cmdline", Encoding.UTF8.GetBytes("an incomplete argv zero"));
        AddProcess(fileSystem, 12, @"C:\Games\KSP2\KSP2_x64.exe");
        fileSystem.File.WriteAllText("/proc/12/status", "Uid:\tmalformed\n");
        AddProcess(fileSystem, 13, @"C:\Games\KSP2\KSP2_x64.exe");
        fileSystem.File.Delete("/proc/13/exe");
        AddProcess(fileSystem, 14, @"C:\Games\KSP2\KSP2_x64.exe");

        Assert.That(GameProcessFinder.FindUnderProton(fileSystem), Is.EqualTo(new[] { 14 }));
    }

    [Test]
    public void WithoutReadableCurrentUserIdentityNoProcessIsSelectedForStopping()
    {
        var fileSystem = NewProc();
        AddProcess(fileSystem, 10, @"C:\Games\KSP2\KSP2_x64.exe");
        fileSystem.File.Delete("/proc/self/status");

        Assert.That(GameProcessFinder.FindUnderProton(fileSystem), Is.Empty);
    }
}
