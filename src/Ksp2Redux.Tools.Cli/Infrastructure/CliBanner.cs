using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Draws the Redux logo on a terminal that can show it.
/// </summary>
// A character cell is about twice as tall as it is wide, so the logo is drawn with half block
// characters: the foreground colour is the upper pixel and the background colour is the lower one.
// That is twice the vertical resolution of one pixel per cell, and it makes the pixels square.
public static class CliBanner
{
    private const string RESOURCE_NAME = "Ksp2Redux.Tools.Cli.logo.png";
    private const int MAX_COLUMNS = 64;
    private const int MINIMUM_COLUMNS = 16;
    private const int COLUMN_MARGIN = 2;
    private const byte ALPHA_THRESHOLD = 32;

    private const char UPPER_HALF = '▀';
    private const char LOWER_HALF = '▄';

    /// <summary>
    /// Writes the logo to stderr, or nothing at all when the stream cannot draw it.
    /// </summary>
    /// <param name="output">The writer the banner is drawn through.</param>
    public static void Write(CliOutput output)
    {
        if (output.IsJson || !output.Capabilities.FancyProgress || !output.Capabilities.ShowProgress)
        {
            return;
        }

        Write(output.ProgressConsole);
    }

    /// <summary>
    /// Writes the logo to a console, for the help screen that runs before there is a command.
    /// </summary>
    /// <param name="console">The console the banner is drawn on.</param>
    public static void Write(IAnsiConsole console)
    {
        try
        {
            if (!console.Profile.Capabilities.Unicode && !EncodingCarriesHalfBlocks())
            {
                WriteWordmark(console);
                return;
            }

            using var resource = typeof(CliBanner).Assembly.GetManifestResourceStream(RESOURCE_NAME);
            if (resource is null)
            {
                return;
            }

            using var logo = Image.Load<Rgba32>(resource);

            var columns = Columns(console);
            var rows = Math.Max(1, (int)Math.Round(columns * (double)logo.Height / logo.Width / 2));
            logo.Mutate(image => image.Resize(columns, rows * 2));

            for (var row = 0; row < rows; row++)
            {
                console.MarkupLine(Line(logo, row));
            }

            console.WriteLine();
        }
        catch (Exception)
        {
            // A banner is decoration. Nothing about it is worth failing a command over.
        }
    }

    // Spectre's unicode capability reports false for plenty of consoles that draw a half block
    // perfectly well, so a no from it is checked against the encoding: can that carry the character
    // back out unchanged. The DOS code pages can, and UTF-8 can. Only when both say no do we give up
    // on the logo and write letters instead.
    private static bool EncodingCarriesHalfBlocks()
    {
        try
        {
            var encoding = Console.OutputEncoding;
            var roundTripped = encoding.GetString(encoding.GetBytes([UPPER_HALF, LOWER_HALF]));
            return roundTripped is [UPPER_HALF, LOWER_HALF];
        }
        catch (Exception)
        {
            return true;
        }
    }

    // A console that cannot carry a half block gets the wordmark as letters rather than nothing.
    private static void WriteWordmark(IAnsiConsole console)
    {
        FigletText wordmark = new("Redux")
        {
            Justification = Justify.Left,
            Color = CliTheme.BRAND_RED,
        };

        console.Write(wordmark);
        console.WriteLine();
    }

    // A cell carries the pixel above and the pixel below. Either one can be transparent, in which
    // case the half block flips or disappears so the terminal's own background shows through.
    private static string Line(Image<Rgba32> logo, int row)
    {
        System.Text.StringBuilder line = new();
        for (var column = 0; column < logo.Width; column++)
        {
            var upper = logo[column, row * 2];
            var lower = logo[column, (row * 2) + 1];

            var hasUpper = upper.A >= ALPHA_THRESHOLD;
            var hasLower = lower.A >= ALPHA_THRESHOLD;

            line.Append((hasUpper, hasLower) switch
            {
                (true, true) => $"[{Hex(upper)} on {Hex(lower)}]{UPPER_HALF}[/]",
                (true, false) => $"[{Hex(upper)}]{UPPER_HALF}[/]",
                (false, true) => $"[{Hex(lower)}]{LOWER_HALF}[/]",
                _ => " ",
            });
        }

        return line.ToString();
    }

    private static string Hex(Rgba32 pixel) => $"#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";

    private static int Columns(IAnsiConsole console)
    {
        var available = console.Profile.Width - COLUMN_MARGIN;
        return Math.Max(MINIMUM_COLUMNS, Math.Min(MAX_COLUMNS, available));
    }
}
