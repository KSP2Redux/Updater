using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliCapabilitiesTest
{
    // A styled document is not a document a parser accepts, so JSON mode has to win over the flag
    // that asks for styling everywhere.
    [Test]
    public void Detect_JsonModeAndForcedColor_LeavesResultsPlain()
    {
        // Act
        CliCapabilities capabilities = CliCapabilities.Detect(ColorMode.Always, isJson: true, isVerbose: false);

        // Assert
        Assert.That(capabilities.FancyResults, Is.False);
        Assert.That(capabilities.FancyProgress, Is.True);
    }

    [Test]
    public void Detect_ColorNever_DrawsNothingAnywhere()
    {
        // Act
        CliCapabilities capabilities = CliCapabilities.Detect(ColorMode.Never, isJson: false, isVerbose: false);

        // Assert
        Assert.That(capabilities.FancyResults, Is.False);
        Assert.That(capabilities.FancyProgress, Is.False);
        Assert.That(capabilities.CanAnimate, Is.False);
        Assert.That(capabilities.CanPrompt, Is.False);
    }

    // The launcher logs to the same stream a progress bar would be redrawing, so the two cannot
    // both be on.
    [Test]
    public void Detect_Verbose_TurnsOffTheLiveDisplay()
    {
        // Act
        CliCapabilities capabilities = CliCapabilities.Detect(ColorMode.Always, isJson: false, isVerbose: true);

        // Assert
        Assert.That(capabilities.FancyProgress, Is.True);
        Assert.That(capabilities.CanAnimate, Is.False);
        Assert.That(capabilities.CanPrompt, Is.False);
    }

    // Quiet is about the CLI's own chrome and verbose is about the launcher's log lines, so the two
    // are set independently rather than fighting each other.
    [Test]
    public void Detect_Quiet_KeepsColorButDropsProgressAndPrompts()
    {
        // Act
        CliCapabilities capabilities = CliCapabilities.Detect(ColorMode.Always, isJson: false, isVerbose: false, isQuiet: true);

        // Assert
        Assert.That(capabilities.FancyProgress, Is.True);
        Assert.That(capabilities.ShowProgress, Is.False);
        Assert.That(capabilities.CanAnimate, Is.False);
        Assert.That(capabilities.CanPrompt, Is.False);
    }

    [Test]
    public void Detect_NotQuiet_ShowsProgress()
    {
        // Act
        CliCapabilities capabilities = CliCapabilities.Detect(ColorMode.Auto, isJson: false, isVerbose: false);

        // Assert
        Assert.That(capabilities.ShowProgress, Is.True);
    }

    [Test]
    public void Plain_IsTheNoStylingCase()
    {
        // Act
        CliCapabilities capabilities = CliCapabilities.Plain;

        // Assert
        Assert.That(capabilities.FancyResults, Is.False);
        Assert.That(capabilities.FancyProgress, Is.False);
        Assert.That(capabilities.CanAnimate, Is.False);
        Assert.That(capabilities.CanPrompt, Is.False);
    }
}
