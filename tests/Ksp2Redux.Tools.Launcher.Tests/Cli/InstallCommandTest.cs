using System.Reflection;
using System.Linq.Expressions;
using Ksp2Redux.Tools.Cli.Commands;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class InstallCommandTest
{
    private const string GAME = @"C:\Games\Kerbal Space Program 2";
    private const string PATCH = @"C:\Downloads\patch.zip";

    internal static readonly Expression<Func<IInstallPlanService, Task>> Apply = p => p.ApplyToFolder(
        It.IsAny<InstallPlan>(),
        It.IsAny<string>(),
        It.IsAny<Action<LogMessage>>(),
        It.IsAny<Action<long, long>>(),
        It.IsAny<Action<int, int>>(),
        It.IsAny<CancellationToken>());

    private static CliCommandHarness WithInstall()
    {
        var harness = new CliCommandHarness();
        harness.SetGameVersion("0.2.2.0.32914");
        harness.Installs.AddInstall(harness.CreateGame(GAME), "Testing");
        harness.FileSystem.Directory.CreateDirectory(@"C:\Downloads");
        harness.FileSystem.File.WriteAllText(PATCH, "zip");
        return harness;
    }

    private static Task<int> InstallPatch(CliCommandHarness harness, bool dryRun = false) =>
        new InstallCommand().RunWithContextAsync(harness.Context, new InstallSettings { PatchFile = PATCH, IsDryRun = dryRun });

    // Both record the installed version in the launcher config, which an open launcher would
    // overwrite with its own copy. The guard itself reads a machine wide mutex, so only the opt in
    // is checked here.
    [TestCase(typeof(InstallCommand))]
    [TestCase(typeof(UpdateCommand))]
    public void RecordsTheInstalledVersion_RefusesWhileTheLauncherIsOpen(Type command)
    {
        // Act
        var changesConfig = command.GetProperty("ChangesConfig", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Activator.CreateInstance(command));

        // Assert
        Assert.That(changesConfig, Is.True);
    }

    [Test]
    public async Task PatchFile_Missing_FailsBeforeTouchingTheInstall()
    {
        // Arrange
        var harness = WithInstall();

        // Act
        var exit = await new InstallCommand().RunWithContextAsync(harness.Context, new InstallSettings { PatchFile = @"C:\typo.zip" });

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.USAGE_ERROR));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.Contain(@"C:\typo.zip"));
        harness.InstallPlan.Verify(Apply, Times.Never);
    }

    [Test]
    public async Task PatchFile_Applied_RecordsTheInstalledVersion()
    {
        // Arrange
        var harness = WithInstall();

        // Act
        var exit = await InstallPatch(harness);

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(harness.Json.GetProperty("installed").GetString(), Is.EqualTo("0.2.2.0.32914"));
        Assert.That(harness.Json.GetProperty("installDir").GetString(), Is.EqualTo(GAME));
        Assert.That(harness.Installs.Entries.Single().LastInstalledVersion?.BuildNumber, Is.EqualTo("32914"));
        harness.InstallPlan.Verify(Apply, Times.Once);
    }

    [Test]
    public async Task PatchFile_DryRun_DescribesWithoutApplying()
    {
        // Arrange
        var harness = WithInstall();

        // Act
        var exit = await InstallPatch(harness, dryRun: true);

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.SUCCESS));
        Assert.That(harness.Json.GetProperty("dryRun").GetBoolean(), Is.True);
        Assert.That(harness.Json.GetProperty("changesNeeded").GetBoolean(), Is.True);
        harness.InstallPlan.Verify(Apply, Times.Never);
        Assert.That(harness.Installs.Entries.Single().LastInstalledVersion, Is.Null);
    }

    [Test]
    public async Task ApplyCancelled_ExitsCancelled()
    {
        // Arrange
        var harness = WithInstall();
        harness.InstallPlan.Setup(Apply).ThrowsAsync(new OperationCanceledException());

        // Act
        var exit = await InstallPatch(harness);

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.CANCELLED));
        Assert.That(harness.Json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.CANCELLED));
        Assert.That(harness.Installs.Entries.Single().LastInstalledVersion, Is.Null);
    }

    [Test]
    public async Task ApplyFailed_ExitsInstallFailedWithTheReason()
    {
        // Arrange
        var harness = WithInstall();
        harness.InstallPlan.Setup(Apply).ThrowsAsync(new InstallFailedException("Disk full.", new IOException(), rolledBack: true));

        // Act
        var exit = await InstallPatch(harness);

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.Json.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Is.EqualTo("Disk full."));
        Assert.That(harness.Installs.Entries.Single().LastInstalledVersion, Is.Null);
    }

    [Test]
    public async Task ApplyThrowsUnexpectedly_ExitsInstallFailedAndWarnsAboutTheState()
    {
        // Arrange
        var harness = WithInstall();
        harness.InstallPlan.Setup(Apply).ThrowsAsync(new InvalidOperationException("Boom."));

        // Act
        var exit = await InstallPatch(harness);

        // Assert
        Assert.That(exit, Is.EqualTo(ExitCode.INSTALL_FAILED));
        Assert.That(harness.Json.GetProperty("error").GetString(), Does.StartWith("Boom.").And.Contain("invalid state"));
    }
}
