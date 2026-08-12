using Ksp2Redux.Tools.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliBannerTest
{
    // The console is supplied rather than detected, because a build agent has no terminal and would
    // otherwise resolve to no colour and draw nothing.
    private static (CliOutput Output, TestConsole Console) Build(bool isJson = false, bool isQuiet = false)
    {
        TestConsole console = new();
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.Unicode = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;

        CliOutput output = new(
            new StringWriter(),
            isJson,
            CliCapabilities.Detect(ColorMode.Always, isJson, isVerbose: false, isQuiet),
            null,
            console);

        return (output, console);
    }

    [Test]
    public void Write_StyledStream_DrawsTheLogoInHalfBlocks()
    {
        // Arrange
        var (output, console) = Build();

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(console.Output, Does.Contain('▀'));
        Assert.That(console.Lines, Has.Count.GreaterThan(4));
    }

    // A pipe gets the same bytes it got before there was a banner.
    [Test]
    public void Write_PlainStream_DrawsNothing()
    {
        // Arrange
        TestConsole console = new();
        CliOutput output = new(new StringWriter(), isJson: false, CliCapabilities.Plain, null, console);

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(console.Output, Is.Empty);
    }

    [Test]
    public void Write_JsonMode_DrawsNothing()
    {
        // Arrange
        var (output, console) = Build(isJson: true);

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(console.Output, Is.Empty);
    }

    [Test]
    public void Write_Quiet_DrawsNothing()
    {
        // Arrange
        var (output, console) = Build(isQuiet: true);

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(console.Output, Is.Empty);
    }
}
