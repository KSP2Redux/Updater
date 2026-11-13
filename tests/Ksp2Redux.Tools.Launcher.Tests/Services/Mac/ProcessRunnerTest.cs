using System.Diagnostics;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Mac;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Mac;

public class ProcessRunnerTest
{
    [SetUp]
    public void RequireAShell() => Assume.That(OperatingSystem.IsWindows(), Is.False, "Needs /bin/sh.");

    [Test]
    public async Task RunAsync_LogsOutputAndErrorLinesAndReturnsTheExitCode()
    {
        List<LogMessage> log = [];

        var exitCode = await new ProcessRunner().RunAsync(
            new ProcessStartInfo("/bin/sh", ["-c", "echo out; echo; echo err >&2; exit 3"]), log.Add, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(log.Select(line => line.English), Is.EquivalentTo(new[] { "out", "err" }));
        });
    }

    [Test]
    public async Task RunAsync_Cancelled_KillsTheProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = Stopwatch.StartNew();

        await Assert.CatchAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(new ProcessStartInfo("/bin/sh", ["-c", "sleep 30"]), _ => { }, cts.Token));
        Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }
}
