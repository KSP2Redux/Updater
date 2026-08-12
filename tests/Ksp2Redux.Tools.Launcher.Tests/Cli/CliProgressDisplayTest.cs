using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliProgressDisplayTest
{
    private const long ONE_MB = 1024L * 1024;

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
    public async Task RunAsync_PlainStream_WritesTheStepAndDownloadLines()
    {
        // Arrange
        CliOutput output = new(new StringWriter(), isJson: false);

        // Act
        await CliProgressDisplay.RunAsync(output, (log, download, steps) =>
        {
            steps(1, 2);
            log("Downloading patch");
            download(64 * ONE_MB, 64 * ONE_MB);
            return Task.CompletedTask;
        });

        // Assert
        Assert.That(_error.ToString(), Does.Contain("step 1 of 2"));
        Assert.That(_error.ToString(), Does.Contain("Downloading patch"));
        Assert.That(_error.ToString(), Does.Contain("downloaded 64 of 64 MB"));
    }

    // Byte level progress arrives every 100ms from the downloader, so only a crossed boundary or
    // the final byte is worth a line.
    [Test]
    public async Task RunAsync_PlainStreamAndSmallSteps_ReportsOnlyTheFinalByte()
    {
        // Arrange
        CliOutput output = new(new StringWriter(), isJson: false);

        // Act
        await CliProgressDisplay.RunAsync(output, (_, download, _) =>
        {
            download(ONE_MB, 100 * ONE_MB);
            download(2 * ONE_MB, 100 * ONE_MB);
            download(100 * ONE_MB, 100 * ONE_MB);
            return Task.CompletedTask;
        });

        // Assert
        string[] lines = _error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(1));
        Assert.That(lines[0].Trim(), Is.EqualTo("downloaded 100 of 100 MB"));
    }

    [Test]
    public async Task RunAsync_AnimatedStream_RunsTheWorkAndDrawsSomething()
    {
        // Arrange
        CliOutput output = new(
            new StringWriter(),
            isJson: false,
            CliCapabilities.Detect(ColorMode.Always, isJson: false, isVerbose: false));
        var ranToCompletion = false;

        // Act
        await CliProgressDisplay.RunAsync(output, (log, download, steps) =>
        {
            steps(1, 1);
            log("Applying patch");
            download(ONE_MB, 2 * ONE_MB);
            ranToCompletion = true;
            return Task.CompletedTask;
        });

        // Assert
        Assert.That(ranToCompletion, Is.True);
        Assert.That(_error.ToString(), Is.Not.Empty);
    }
}
