namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// One cell of a table, with the style it takes when the stream is a terminal.
/// </summary>
/// <param name="Text">The cell text, written verbatim and never read as markup.</param>
/// <param name="Style">A Spectre style such as "bold", or null for the default.</param>
/// <param name="IsPath">
/// True to draw the cell as a file path, which shortens from the middle on a terminal too narrow
/// to hold it. Plain output always writes the path in full.
/// </param>
public readonly record struct CliCell(string Text, string? Style = null, bool IsPath = false)
{
    /// <summary>
    /// Wraps plain text as an unstyled cell.
    /// </summary>
    /// <param name="text">The cell text.</param>
    public static implicit operator CliCell(string text) => new(text);

    /// <summary>
    /// Wraps plain text as an unstyled cell.
    /// </summary>
    /// <param name="text">The cell text.</param>
    /// <returns>A cell holding <paramref name="text" /> with no style.</returns>
    public static CliCell FromString(string text) => new(text);

    /// <summary>
    /// Wraps a file path as a cell that may be shortened to fit.
    /// </summary>
    /// <param name="path">The path to show.</param>
    /// <returns>A cell holding <paramref name="path" />.</returns>
    public static CliCell Path(string path) => new(path, IsPath: true);
}
