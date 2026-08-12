using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Factory for the Spectre consoles the CLI draws through.
/// </summary>
public static class CliConsole
{
    private const int FALLBACK_WIDTH = 100;

    /// <summary>
    /// Builds a console over one stream.
    /// </summary>
    /// <param name="writer">The stream the console writes to.</param>
    /// <param name="isFancy">True to emit color and box drawing, false to emit plain text.</param>
    /// <param name="isInteractive">True when the console may read keys from the user.</param>
    /// <returns>A console bound to <paramref name="writer" />.</returns>
    public static IAnsiConsole Create(TextWriter writer, bool isFancy, bool isInteractive)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = isFancy ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = isFancy ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = isInteractive ? InteractionSupport.Yes : InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        if (isFancy)
        {
            console.Profile.Width = TerminalWidth();
        }

        return console;
    }

    private static int TerminalWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : FALLBACK_WIDTH;
        }
        catch (IOException)
        {
            return FALLBACK_WIDTH;
        }
        catch (PlatformNotSupportedException)
        {
            return FALLBACK_WIDTH;
        }
    }
}
