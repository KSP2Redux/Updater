using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CacheClearCommandTest
{
    [Test]
    public async Task FileThatCannotBeDeleted_FailsAndSaysSo()
    {
        // Arrange
        CliCommandHarness harness = new();
        var directory = harness.Context.DownloadCacheDirectory;
        harness.FileSystem.Directory.CreateDirectory(directory);
        harness.FileSystem.File.WriteAllText(harness.FileSystem.Path.Combine(directory, "free.json"), "{}");
        var locked = harness.FileSystem.Path.Combine(directory, "locked.patch");
        harness.FileSystem.File.WriteAllText(locked, "patch");
        using var handle = harness.FileSystem.File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        var exit = await new CacheClearCommand().RunWithContextAsync(harness.Context, new CacheClearSettings { AssumeYes = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.FileSystem.File.Exists(locked), Is.True);
    }

    [Test]
    public async Task EveryFileDeleted_Succeeds()
    {
        // Arrange
        CliCommandHarness harness = new();
        var directory = harness.Context.DownloadCacheDirectory;
        harness.FileSystem.Directory.CreateDirectory(directory);
        harness.FileSystem.File.WriteAllText(harness.FileSystem.Path.Combine(directory, "free.json"), "{}");

        // Act
        var exit = await new CacheClearCommand().RunWithContextAsync(harness.Context, new CacheClearSettings { AssumeYes = true });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(harness.Json.GetProperty("deleted").GetInt32(), Is.EqualTo(1));
    }
}
