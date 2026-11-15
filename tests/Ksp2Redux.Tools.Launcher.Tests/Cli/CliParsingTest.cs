using Ksp2Redux.Tools.Cli.Settings;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

/// <summary>Parses command lines against the real settings types, without running any command.</summary>
public class CliParsingTest
{
    [TestCase("")]
    [TestCase("  ")]
    public void InstallsDelete_BlankInstall_IsRejected(string install)
    {
        // Act
        var exit = Parse<InstallsDeleteSettings>(["installs", "delete", install, "--yes"]);

        // Assert
        Assert.That(exit, Is.Not.Zero);
    }

    [TestCase("")]
    [TestCase("  ")]
    public void InstallOption_Blank_IsRejected(string install)
    {
        BaseInstallSettings[] settings =
        [
            new UninstallSettings { Install = install }, new UpdateSettings { Install = install },
            new InstallSettings { Install = install }, new LaunchSettings { Install = install },
        ];

        Assert.That(settings.Select(s => s.Validate().Successful), Is.All.False);
    }

    [Test]
    public void InstallOption_LeftOut_UsesTheActiveInstall() =>
        Assert.That(new UninstallSettings().Validate().Successful, Is.True);

    [Test]
    public void InstallsDelete_NamedInstall_Parses()
    {
        // Act
        var exit = Parse<InstallsDeleteSettings>(["installs", "delete", "Testing", "--yes"]);

        // Assert
        Assert.That(exit, Is.Zero);
    }

    /// <summary>Runs a two word command line against a probe command bound to the real settings type.</summary>
    internal static int Parse<TSettings>(string[] args)
        where TSettings : CommandSettings
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.ConfigureConsole(new TestConsole());
            config.UseStrictParsing();
            config.AddBranch(args[0], branch => branch.AddCommand<Probe<TSettings>>(args[1]));
        });

        return app.Run(args);
    }

    private sealed class Probe<TSettings> : Command<TSettings>
        where TSettings : CommandSettings
    {
        public override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken) => 0;
    }
}
