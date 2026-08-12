using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliBannerTest
{
    private TextWriter _originalError = null!;
    private StringWriter _error = null!;

    [SetUp]
    public void SetUp()
    {
        _originalError = Console.Error;
        _error = new StringWriter();
        Console.SetError(_error);
    }

    [TearDown]
    public void TearDown()
    {
        Console.SetError(_originalError);
        _error.Dispose();
    }

    [Test]
    public void Write_StyledStream_DrawsTheLogo()
    {
        // Arrange
        CliOutput output = new(
            new StringWriter(),
            isJson: false,
            CliCapabilities.Detect(ColorMode.Always, isJson: false, isVerbose: false));

        // Act
        CliBanner.Write(output);

        // Assert
        string drawn = _error.ToString();

        Assert.That(drawn, Does.Contain("38;2;"), "the upper half of a cell carries a colour");

        Assert.That(drawn, Does.Contain("48;2;"), "the lower half of a cell carries a colour");

        Assert.That(drawn, Does.Contain('▀'), "the logo is drawn with half blocks");
    }

    // A pipe gets the same bytes it got before there was a banner.
    [Test]
    public void Write_PlainStream_DrawsNothing()
    {
        // Arrange
        CliOutput output = new(new StringWriter(), isJson: false);

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(_error.ToString(), Is.Empty);
    }

    [Test]
    public void Write_JsonMode_DrawsNothing()
    {
        // Arrange
        CliOutput output = new(
            new StringWriter(),
            isJson: true,
            CliCapabilities.Detect(ColorMode.Always, isJson: true, isVerbose: false));

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(_error.ToString(), Is.Empty);
    }

    [Test]
    public void Write_Quiet_DrawsNothing()
    {
        // Arrange
        CliOutput output = new(
            new StringWriter(),
            isJson: false,
            CliCapabilities.Detect(ColorMode.Always, isJson: false, isVerbose: false, isQuiet: true));

        // Act
        CliBanner.Write(output);

        // Assert
        Assert.That(_error.ToString(), Is.Empty);
    }
}
