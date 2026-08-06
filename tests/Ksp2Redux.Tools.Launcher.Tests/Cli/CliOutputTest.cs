using System.Text.Json;
using Ksp2Redux.Tools.Cli;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliOutputTest
{
    private static (CliOutput Output, StringWriter Results) Build(bool isJson)
    {
        StringWriter results = new();
        return (new CliOutput(results, isJson), results);
    }

    [Test]
    public void Result_TextMode_WritesTheLine()
    {
        // Arrange
        var (output, results) = Build(isJson: false);

        // Act
        output.Result("0.2.9.0.103669");

        // Assert
        Assert.That(results.ToString().Trim(), Is.EqualTo("0.2.9.0.103669"));
    }

    // A verb's text output would otherwise land in the middle of the JSON document.
    [Test]
    public void Result_JsonMode_WritesNothing()
    {
        // Arrange
        var (output, results) = Build(isJson: true);

        // Act
        output.Result("0.2.9.0.103669");

        // Assert
        Assert.That(results.ToString(), Is.Empty);
    }

    [Test]
    public void Payload_TextMode_RunsTheTextFallbackOnly()
    {
        // Arrange
        var (output, results) = Build(isJson: false);
        var ranFallback = false;

        // Act
        output.Payload(new { build = "103669" }, () =>
        {
            ranFallback = true;
            output.Result("103669");
        });

        // Assert
        Assert.That(ranFallback, Is.True);
        Assert.That(results.ToString().Trim(), Is.EqualTo("103669"));
    }

    [Test]
    public void Payload_JsonMode_SerializesAndSkipsTheFallback()
    {
        // Arrange
        var (output, results) = Build(isJson: true);
        var ranFallback = false;

        // Act
        output.Payload(new { build = "103669" }, () => ranFallback = true);

        // Assert
        Assert.That(ranFallback, Is.False);
        using JsonDocument document = JsonDocument.Parse(results.ToString());
        Assert.That(document.RootElement.GetProperty("build").GetString(), Is.EqualTo("103669"));
    }

    [Test]
    public void Fail_ReturnsTheExitCodeItWasGiven()
    {
        // Arrange
        var (output, _) = Build(isJson: false);

        // Act
        int result = output.Fail(ExitCode.VERSION_NOT_FOUND, "nope");

        // Assert
        Assert.That(result, Is.EqualTo(ExitCode.VERSION_NOT_FOUND));
    }

    [Test]
    public void Fail_TextMode_LeavesTheResultStreamEmpty()
    {
        // Arrange
        var (output, results) = Build(isJson: false);

        // Act
        output.Fail(ExitCode.FEED_NOT_CONFIGURED, "no feed");

        // Assert
        Assert.That(results.ToString(), Is.Empty);
    }

    // A script parsing the result stream has to see the failure rather than an empty document.
    [Test]
    public void Fail_JsonMode_WritesAnErrorDocument()
    {
        // Arrange
        var (output, results) = Build(isJson: true);

        // Act
        output.Fail(ExitCode.FEED_NOT_CONFIGURED, "no feed");

        // Assert
        using JsonDocument document = JsonDocument.Parse(results.ToString());
        Assert.That(document.RootElement.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(document.RootElement.GetProperty("exitCode").GetInt32(), Is.EqualTo(ExitCode.FEED_NOT_CONFIGURED));
        Assert.That(document.RootElement.GetProperty("error").GetString(), Is.EqualTo("no feed"));
    }

    [Test]
    public void Table_ShortAndLongCells_PadsEveryColumnToItsWidest()
    {
        // Arrange
        var (output, results) = Build(isJson: false);

        // Act
        output.Table(["BUILD", "VERSION"], [
            ["101669", "0.2.3.0.101669"],
            ["1", "x"],
        ]);

        // Assert
        string[] lines = results.ToString().TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.That(lines, Has.Length.EqualTo(4));
        Assert.That(lines[0], Is.EqualTo("BUILD   VERSION"));
        Assert.That(lines[1], Is.EqualTo("------  --------------"));
        Assert.That(lines[2], Is.EqualTo("101669  0.2.3.0.101669"));
        Assert.That(lines[3], Is.EqualTo("1       x"));
    }

    [Test]
    public void Table_JsonMode_StillWritesText()
    {
        // Arrange
        var (output, results) = Build(isJson: true);

        // Act
        output.Table(["A"], [["1"]]);

        // Assert
        Assert.That(results.ToString(), Is.Not.Empty);
    }
}
