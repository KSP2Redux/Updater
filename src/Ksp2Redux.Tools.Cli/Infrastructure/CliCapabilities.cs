namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// What the CLI is allowed to draw, decided per stream.
/// </summary>
public sealed class CliCapabilities
{
    private CliCapabilities(bool fancyResults, bool fancyProgress, bool canAnimate, bool canPrompt, bool showProgress)
    {
        FancyResults = fancyResults;
        FancyProgress = fancyProgress;
        CanAnimate = canAnimate;
        CanPrompt = canPrompt;
        ShowProgress = showProgress;
    }

    /// <summary>
    /// Gets the capabilities of a stream that carries plain text and nothing else.
    /// </summary>
    public static CliCapabilities Plain { get; } = new(false, false, false, false, true);

    /// <summary>
    /// Gets a value indicating whether progress, headings and detail lines are written at all.
    /// </summary>
    public bool ShowProgress { get; }

    /// <summary>
    /// Gets a value indicating whether results on stdout may be drawn as styled tables.
    /// </summary>
    public bool FancyResults { get; }

    /// <summary>
    /// Gets a value indicating whether progress on stderr may be coloured.
    /// </summary>
    public bool FancyProgress { get; }

    /// <summary>
    /// Gets a value indicating whether stderr may carry a live progress bar or spinner.
    /// </summary>
    public bool CanAnimate { get; }

    /// <summary>
    /// Gets a value indicating whether the user can be asked to pick from a list.
    /// </summary>
    public bool CanPrompt { get; }

    /// <summary>
    /// Works out what may be drawn for one command run.
    /// </summary>
    /// <param name="mode">The mode named by the color option.</param>
    /// <param name="isJson">True when stdout carries a JSON document.</param>
    /// <param name="isVerbose">True when the launcher's log lines are being printed to stderr.</param>
    /// <param name="isQuiet">True when progress, headings and detail lines are suppressed.</param>
    /// <returns>The capabilities for this run.</returns>
    public static CliCapabilities Detect(ColorMode mode, bool isJson, bool isVerbose, bool isQuiet = false)
    {
        var styleStdout = mode switch
        {
            ColorMode.Always => true,
            ColorMode.Never => false,
            _ => !Console.IsOutputRedirected,
        };

        var styleStderr = mode switch
        {
            ColorMode.Always => true,
            ColorMode.Never => false,
            _ => !Console.IsErrorRedirected,
        };

        var canAnimate = styleStderr && !isVerbose && !isQuiet;
        var canPrompt = canAnimate && !isJson && !Console.IsInputRedirected;

        return new CliCapabilities(styleStdout && !isJson, styleStderr, canAnimate, canPrompt, !isQuiet);
    }
}
