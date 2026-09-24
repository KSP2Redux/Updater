using Ksp2Redux.Tools.Cli.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

// The first terminal QR code came out inverted, dark modules drawn white, which phone scanners reject.
// The light modules, quiet zone included, are the ones drawn, so they show white on black.
public class CliQrCodeTest
{
    private const string URL = "https://s.team/q/1/7394829384729384729";

    [Test]
    public void Lines_QuietZone_IsDrawnLight()
    {
        // Act
        var lines = CliQrCode.Lines(URL);

        // Assert
        Assert.That(lines[0], Is.EqualTo(new string('\u2588', lines[0].Length)));
        Assert.That(lines[^1].Trim('\u2588', '\u2580'), Is.Empty);
    }

    // The top-left finder pattern starts four modules in, after the quiet zone, with a dark outer ring.
    [Test]
    public void Lines_FinderPatternCorner_IsDark()
    {
        // Act
        var lines = CliQrCode.Lines(URL);

        // Assert
        Assert.That(lines[2][4], Is.EqualTo(' '));
        Assert.That(lines[2][3], Is.EqualTo('\u2588'));
    }

    [Test]
    public void Lines_EveryRow_IsTheSameWidthAndTwoModulesTall()
    {
        // Act
        var lines = CliQrCode.Lines(URL);

        // Assert
        Assert.That(lines.Select(line => line.Length).Distinct(), Has.Exactly(1).Items);
        Assert.That(lines.Count, Is.EqualTo((lines[0].Length + 1) / 2));
    }
}
