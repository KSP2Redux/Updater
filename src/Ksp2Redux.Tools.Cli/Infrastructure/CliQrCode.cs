using QRCoder;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Draws a QR code in a terminal, two modules per character cell.
/// </summary>
public static class CliQrCode
{
    private const char BOTH_LIGHT = '█';
    private const char TOP_LIGHT = '▀';
    private const char BOTTOM_LIGHT = '▄';
    private const char BOTH_DARK = ' ';

    /// <summary>
    /// Encodes <paramref name="text" /> as the lines of a QR code made of half block characters.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <returns>One string per terminal row, all the same length, quiet zone included.</returns>
    // The light modules, quiet zone included, are the ones drawn, so the code comes out the right way
    // round when those characters are painted white on black. Scanners want dark modules on light.
    public static IReadOnlyList<string> Lines(string text)
    {
        using QRCodeGenerator generator = new();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;

        bool IsLight(int row, int column) => row >= size || !matrix[row][column];

        List<string> lines = [];
        for (var row = 0; row < size; row += 2)
        {
            var line = new char[size];
            for (var column = 0; column < size; column++)
            {
                line[column] = (IsLight(row, column), IsLight(row + 1, column)) switch
                {
                    (true, true) => BOTH_LIGHT,
                    (true, false) => TOP_LIGHT,
                    (false, true) => BOTTOM_LIGHT,
                    _ => BOTH_DARK,
                };
            }

            lines.Add(new string(line));
        }

        return lines;
    }

    /// <summary>
    /// Builds a renderable QR code, painted white on black so phones can scan it on any terminal theme.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <returns>The QR code as rows of markup.</returns>
    public static IRenderable Render(string text) =>
        new Rows(Lines(text).Select(line => new Markup($"[white on black]{Markup.Escape(line)}[/]")));
}
