using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Linux;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Linux;

public class ProtonRuntimeServiceTest
{
    private const string HOME = "/home/player";
    private const string STORAGE = "/home/player/.local/share/Redux launcher";
    private const string STEAM = "/home/player/.local/share/Steam";

    private static MockFileSystem NewFileSystem() => new(options => options.SimulatingOperatingSystem(SimulationMode.Linux));

    private static ProtonRuntimeService Build(MockFileSystem fileSystem, Mock<IProcessRunner>? runner = null,
        HttpClient? client = null, string? dataHome = null)
    {
        var environment = new Mock<IEnvironmentProvider>();
        environment.Setup(value => value.GetFolderPath(Environment.SpecialFolder.UserProfile)).Returns(HOME);
        environment.Setup(value => value.GetEnvironmentVariable("XDG_DATA_HOME")).Returns(dataHome);
        var config = new Mock<ILauncherConfigService>();
        config.Setup(value => value.GetLocalStorageDirectory()).Returns(STORAGE);
        return new ProtonRuntimeService(fileSystem, environment.Object, config.Object,
            (runner ?? new Mock<IProcessRunner>()).Object, new Mock<ILogService>().Object, client);
    }

    private static void AddTool(MockFileSystem fileSystem, string directory, string runtime = "1628350")
    {
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(directory, "files", "bin"));
        foreach (string file in new[] { "proton", "version", "files/bin/wine", "files/bin/wineserver" })
        {
            fileSystem.File.WriteAllText(fileSystem.Path.Combine(directory, file), "installed runtime data");
        }
        fileSystem.File.WriteAllText(fileSystem.Path.Combine(directory, "toolmanifest.vdf"),
            "\"manifest\" { \"commandline\" \"/proton %verb%\" \"require_tool_appid\" \"" + runtime +
            "\" \"compatmanager_layer_name\" \"proton\" }");
    }

    private static byte[] Archive(string name, byte[] contents, TarEntryType type = TarEntryType.RegularFile)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.SymbolicLink)
                entry.LinkName = "../../outside";
            else
                entry.DataStream = new MemoryStream(contents);
            writer.WriteEntry(entry);
        }
        return stream.ToArray();
    }

    private static string Hash(byte[] contents) => Convert.ToHexString(SHA256.HashData(contents));

    [Test]
    public void VerifiedArchiveCopiesOnlyTheExpectedBootstrapBytes()
    {
        byte[] bootstrap = Encoding.UTF8.GetBytes("a synthetic Python bootstrap");
        byte[] archive = Archive("umu/umu-run", bootstrap);

        byte[] extracted = ProtonRuntimeService.ReadBootstrap(archive, Hash(archive), Hash(bootstrap));

        Assert.That(extracted, Is.EqualTo(bootstrap));
    }

    [Test]
    public void ChangedArchiveIsRejectedBeforeParsingOrRunningIt()
    {
        byte[] bootstrap = Encoding.UTF8.GetBytes("synthetic bootstrap");
        byte[] archive = Archive("umu/umu-run", bootstrap);
        string trustedHash = Hash(archive);
        archive[600] ^= 1;

        Assert.Throws<InvalidOperationException>(() => ProtonRuntimeService.ReadBootstrap(archive, trustedHash, Hash(bootstrap)));
    }

    [Test]
    public void ChangedBootstrapCannotBePublishedEvenInAChecksummedArchive()
    {
        byte[] bootstrap = Encoding.UTF8.GetBytes("wrong bootstrap");
        byte[] archive = Archive("umu/umu-run", bootstrap);

        Assert.Throws<InvalidOperationException>(() => ProtonRuntimeService.ReadBootstrap(archive, Hash(archive), Hash(Encoding.UTF8.GetBytes("trusted bootstrap"))));
    }

    [TestCase("../../outside")]
    [TestCase("/umu/umu-run")]
    [TestCase("umu/../umu-run")]
    public void OtherArchivePathsCannotBecomeTheRuntimeBootstrap(string entryName)
    {
        byte[] bootstrap = Encoding.UTF8.GetBytes("wrong entry");
        byte[] archive = Archive(entryName, bootstrap);

        Assert.Throws<InvalidOperationException>(() => ProtonRuntimeService.ReadBootstrap(archive, Hash(archive), Hash(bootstrap)));
    }

    [Test]
    public void ArchiveSymlinksAreRejectedInsteadOfFollowingTheirTargets()
    {
        byte[] archive = Archive("umu/umu-run", [], TarEntryType.SymbolicLink);

        Assert.Throws<InvalidOperationException>(() => ProtonRuntimeService.ReadBootstrap(archive, Hash(archive), Hash([])));
    }

    [Test]
    public void SteamLibraryProtonIsReusedWithoutWritingToTheSteamInstallation()
    {
        var fileSystem = NewFileSystem();
        string proton = "/mnt/Steam library/steamapps/common/Proton 10.0";
        AddTool(fileSystem, proton);
        fileSystem.Directory.CreateDirectory(STEAM + "/steamapps");
        fileSystem.File.WriteAllText(STEAM + "/steamapps/libraryfolders.vdf",
            "\"libraryfolders\" { \"1\" { \"path\" \"/mnt/Steam library\" } }");
        string originalManifest = fileSystem.File.ReadAllText(proton + "/toolmanifest.vdf");
        using var service = Build(fileSystem);

        Assert.That(service.FindInstalledProton(), Is.EqualTo(proton));
        Assert.That(fileSystem.File.ReadAllText(proton + "/toolmanifest.vdf"), Is.EqualTo(originalManifest));
        Assert.That(fileSystem.Directory.Exists(STORAGE), Is.False, "Discovery must not create a prefix or modify Steam.");
    }

    [TestCase(STEAM)]
    [TestCase("/home/player/.steam/steam")]
    [TestCase("/home/player/.steam/root")]
    [TestCase("/home/player/.var/app/com.valvesoftware.Steam/data/Steam")]
    [TestCase("/mnt/xdg-data/Steam")]
    public void InstalledCustomSteamToolsAreFoundAtNativeFlatpakAndXdgRoots(string root)
    {
        var fileSystem = NewFileSystem();
        string proton = root + "/compatibilitytools.d/GE-Proton10-27";
        AddTool(fileSystem, proton);
        using var service = Build(fileSystem, dataHome: "/mnt/xdg-data");

        Assert.That(service.FindInstalledProton(), Is.EqualTo(proton));
    }

    [Test]
    public void NewestNumberedStableToolIsPreferredOverOldExperimentalAndIncompleteTools()
    {
        var fileSystem = NewFileSystem();
        foreach (string name in new[] { "GE-Proton9-50", "GE-Proton10-3", "GE-Proton10-27", "Proton - Experimental", "Proton 7.0", "GE-Proton11-1" })
        {
            AddTool(fileSystem, STEAM + "/compatibilitytools.d/" + name);
        }
        fileSystem.File.Delete(STEAM + "/compatibilitytools.d/GE-Proton11-1/files/bin/wine");
        using var service = Build(fileSystem);

        Assert.That(service.FindInstalledProton(), Is.EqualTo(STEAM + "/compatibilitytools.d/GE-Proton10-27"));
    }

    [TestCase("4185400")]
    [TestCase("1391110")]
    [TestCase("unknown-runtime")]
    public void UnsupportedContainerOrArchitectureFallsBackToManagedDownloads(string runtime)
    {
        var fileSystem = NewFileSystem();
        AddTool(fileSystem, STEAM + "/compatibilitytools.d/GE-Proton10-27", runtime);
        using var service = Build(fileSystem);

        Assert.That(service.FindInstalledProton(), Is.Null);
    }

    [Test]
    public void CommentedManifestFieldsCannotMakeAnUnsupportedToolEligible()
    {
        var fileSystem = NewFileSystem();
        string proton = STEAM + "/compatibilitytools.d/GE-Proton10-27";
        AddTool(fileSystem, proton);
        fileSystem.File.WriteAllText(proton + "/toolmanifest.vdf", """
            "manifest" {
                // "require_tool_appid" "1628350"
                "require_tool_appid" "4185400"
                "commandline" "/proton %verb%"
                "compatmanager_layer_name" "proton"
            }
            """);
        using var service = Build(fileSystem);

        Assert.That(service.FindInstalledProton(), Is.Null);
    }

    [Test]
    public async Task PreparationAndGameLaunchUseTheSameReusableProtonWithADedicatedPrefix()
    {
        var fileSystem = NewFileSystem();
        string proton = STEAM + "/compatibilitytools.d/GE-Proton10-27";
        AddTool(fileSystem, proton);
        var prepared = new List<string?>();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartInfo, Action<LogMessage>, CancellationToken>((info, _, _) =>
            {
                prepared.Add(info.Environment["PROTONPATH"]);
                string prefix = info.Environment["WINEPREFIX"]!;
                fileSystem.File.WriteAllText(fileSystem.Path.Combine(prefix, "system.reg"), "initialized prefix");
            }).ReturnsAsync(0);
        using var service = Build(fileSystem, runner);

        await service.PrepareRuntimeAsync(_ => { }, CancellationToken.None);
        ProcessStartInfo launch = service.CreateLaunchInfo("/Games/KSP2/KSP2_x64.exe", "/Games/KSP2", null);

        Assert.That(prepared, Is.EqualTo(new[] { proton }));
        Assert.That(launch.Environment["PROTONPATH"], Is.EqualTo(proton));
        Assert.That(launch.Environment["WINEPREFIX"], Does.StartWith(STORAGE));
        Assert.That(launch.Environment["WINEPREFIX"], Does.Not.Contain("compatdata"));
        Assert.That(service.GetGameDataFolder(), Does.StartWith(launch.Environment["WINEPREFIX"]!));
        Assert.That(fileSystem.Directory.Exists(proton + "/proton-prefix"), Is.False);
    }

    [Test]
    public async Task FailingInstalledProtonRetriesWithManagedProtonAndRetainsThatChoiceForLaunch()
    {
        var fileSystem = NewFileSystem();
        string proton = STEAM + "/compatibilitytools.d/GE-Proton10-27";
        AddTool(fileSystem, proton);
        var attempted = new List<string?>();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Returns<ProcessStartInfo, Action<LogMessage>, CancellationToken>((info, _, _) =>
            {
                attempted.Add(info.Environment["PROTONPATH"]);
                if (attempted.Count == 1)
                    return Task.FromResult(1);
                fileSystem.File.WriteAllText(STORAGE + "/proton-prefix/system.reg", "initialized prefix");
                return Task.FromResult(0);
            });
        using var service = Build(fileSystem, runner);

        await service.PrepareRuntimeAsync(_ => { }, CancellationToken.None);

        Assert.That(attempted, Is.EqualTo(new[] { proton, "GE-Proton" }));
        Assert.That(service.CreateLaunchInfo("/Games/KSP2/KSP2_x64.exe", "/Games/KSP2", null).Environment["PROTONPATH"], Is.EqualTo("GE-Proton"));
        Assert.That(fileSystem.File.ReadAllText(proton + "/proton"), Is.EqualTo("installed runtime data"));
    }

    [Test]
    public async Task CancellingInstalledProtonPreparationDoesNotStartADownloadFallback()
    {
        var fileSystem = NewFileSystem();
        AddTool(fileSystem, STEAM + "/compatibilitytools.d/GE-Proton10-27");
        using var cancelled = new CancellationTokenSource();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .Returns<ProcessStartInfo, Action<LogMessage>, CancellationToken>((_, _, _) =>
            {
                cancelled.Cancel();
                return Task.FromCanceled<int>(cancelled.Token);
            });
        using var service = Build(fileSystem, runner);

        await Assert.CatchAsync<OperationCanceledException>(() => service.PrepareRuntimeAsync(_ => { }, cancelled.Token));
        runner.Verify(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task MissingPythonDoesNotDownloadOrStartTheRuntime()
    {
        var fileSystem = NewFileSystem();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Win32Exception("python3 was not found"));
        var handler = new DownloadHandler(_ => throw new AssertionException("Python must be checked before downloading."));
        using var client = new HttpClient(handler);
        using var service = Build(fileSystem, runner, client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(_ => { }, CancellationToken.None));
        Assert.That(fileSystem.Directory.Exists(STORAGE), Is.False);
    }

    [Test]
    public async Task IncorrectDownloadCannotReplaceTheCachedBootstrapOrStartProton()
    {
        var fileSystem = NewFileSystem();
        string cached = STORAGE + "/proton-runtime/bootstrap/1.4.4/umu-run.pyz";
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(cached)!);
        fileSystem.File.WriteAllText(cached, "previous incomplete download");
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        using var client = new HttpClient(new DownloadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("invalid archive"))
        }));
        using var service = Build(fileSystem, runner, client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(_ => { }, CancellationToken.None));

        Assert.That(fileSystem.File.ReadAllText(cached), Is.EqualTo("previous incomplete download"));
        runner.Verify(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(fileSystem.Directory.Exists(STORAGE + "/proton-prefix"), Is.False);
    }

    [Test]
    public async Task CancelledDownloadLeavesNoPublishedBootstrapAndCanBeRetried()
    {
        var fileSystem = NewFileSystem();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        using var cancelled = new CancellationTokenSource();
        var handler = new DownloadHandler(_ =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(cancelled.Token);
        });
        using var client = new HttpClient(handler);
        using var service = Build(fileSystem, runner, client);

        await Assert.CatchAsync<OperationCanceledException>(() => service.PrepareAsync(_ => { }, cancelled.Token));
        Assert.That(fileSystem.Directory.Exists(STORAGE), Is.False);
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PrepareAsync(_ => { }, CancellationToken.None));
        Assert.That(handler.Requests, Is.EqualTo(2), "Cancellation must release the preparation gate for another attempt.");
    }

    [Test]
    public async Task OversizedDownloadIsRejectedBeforeReadingItsBody()
    {
        var fileSystem = NewFileSystem();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.RunAsync(It.IsAny<ProcessStartInfo>(), It.IsAny<Action<LogMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var content = new ByteArrayContent([]);
        content.Headers.ContentLength = 16 * 1024 * 1024;
        using var client = new HttpClient(new DownloadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var service = Build(fileSystem, runner, client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(_ => { }, CancellationToken.None));
        Assert.That(fileSystem.Directory.Exists(STORAGE), Is.False);
    }

    [TestCase("")]
    [TestCase("/home/player/Games/a path with spaces/KSP2_x64.exe")]
    [TestCase("C:\\quoted path\\with a trailing slash\\")]
    [TestCase("quoted \"value\" and literal $(shell); & <text>")]
    [Platform("Linux")]
    public async Task QuotedArgumentsRoundTripThroughTheNativeProcessParserWithoutAShell(string argument)
    {
        var processInfo = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            Arguments = "-I -c " + ProtonRuntimeService.QuoteArgument("import json,sys; print(json.dumps(sys.argv[1:]))") +
                        " " + ProtonRuntimeService.QuoteArgument(argument)
        };
        using var process = Process.Start(processInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.That(process.ExitCode, Is.Zero);
        Assert.That(System.Text.Json.JsonSerializer.Deserialize<string[]>(output), Is.EqualTo(new[] { argument }));
    }

    private sealed class DownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Respond(request));
        }
    }
}
