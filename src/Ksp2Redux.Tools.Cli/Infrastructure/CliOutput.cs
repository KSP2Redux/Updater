using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Writer for everything the commands print.
/// </summary>
// Results go to the result stream and progress goes to stderr, so a script can capture one without
// the other and the JSON document stays parseable even while an install logs its steps. The result
// stream is passed in rather than read off Console, because the launcher's own LogService writes to
// Console.Out and the CLI redirects that to stderr to keep the data channel clean.
public sealed class CliOutput
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const int MINIMUM_PATH_WIDTH = 24;



    private readonly TextWriter _results;

    /// <summary>
    /// Initializes a writer that emits plain text only, for a stream that is not a terminal.
    /// </summary>
    /// <param name="results">The stream results are written to, normally the process's real stdout.</param>
    /// <param name="isJson">True to emit a JSON document, false for human readable text.</param>
    public CliOutput(TextWriter results, bool isJson)
        : this(results, isJson, CliCapabilities.Plain)
    {
    }

    /// <summary>
    /// Initializes a writer over the stream that carries results, in either text or JSON mode.
    /// </summary>
    /// <param name="results">The stream results are written to, normally the process's real stdout.</param>
    /// <param name="isJson">True to emit a JSON document, false for human readable text.</param>
    /// <param name="capabilities">What the two streams are allowed to draw.</param>
    public CliOutput(TextWriter results, bool isJson, CliCapabilities capabilities)
        : this(results, isJson, capabilities, null, null)
    {
    }

    /// <summary>
    /// Initializes a writer over consoles supplied by the caller, for tests.
    /// </summary>
    /// <param name="results">The stream results are written to.</param>
    /// <param name="isJson">True to emit a JSON document, false for human readable text.</param>
    /// <param name="capabilities">What the two streams are allowed to draw.</param>
    /// <param name="resultConsole">The console results are drawn on, or null to build one.</param>
    /// <param name="progressConsole">The console progress is drawn on, or null to build one.</param>
    public CliOutput(
        TextWriter results,
        bool isJson,
        CliCapabilities capabilities,
        IAnsiConsole? resultConsole,
        IAnsiConsole? progressConsole)
    {
        _results = results;
        IsJson = isJson;
        Capabilities = capabilities;
        ResultConsole = resultConsole ?? CliConsole.Create(results, capabilities.FancyResults, isInteractive: false);
        ProgressConsole = progressConsole ?? CliConsole.Create(Console.Error, capabilities.FancyProgress, capabilities.CanAnimate);
    }

    /// <summary>
    /// Gets a value indicating whether the result stream carries a JSON document rather than text.
    /// </summary>
    public bool IsJson { get; }

    /// <summary>
    /// Gets what the two streams are allowed to draw.
    /// </summary>
    public CliCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the console that draws results on stdout.
    /// </summary>
    public IAnsiConsole ResultConsole { get; }

    /// <summary>
    /// Gets the console that draws progress, prompts and errors on stderr.
    /// </summary>
    public IAnsiConsole ProgressConsole { get; }

    /// <summary>
    /// Writes a line of text to stdout, or nothing at all in JSON mode.
    /// </summary>
    /// <param name="line">The line to write.</param>
    public void Result(string line)
    {
        if (IsJson)
        {
            return;
        }

        _results.WriteLine(line);
    }

    /// <summary>
    /// Serializes a payload to stdout in JSON mode, otherwise runs the text fallback.
    /// </summary>
    /// <param name="payload">The object to serialize in JSON mode.</param>
    /// <param name="writeText">The fallback that writes the human readable form.</param>
    public void Payload(object payload, Action writeText)
    {
        if (IsJson)
        {
            _results.WriteLine(JsonSerializer.Serialize(payload, JSON_OPTIONS));
            return;
        }

        writeText();
    }

    /// <summary>
    /// Writes a progress line to stderr in both modes.
    /// </summary>
    /// <param name="line">The line to write.</param>
    public void Progress(string line)
    {
        if (!Capabilities.ShowProgress)
        {
            return;
        }

        if (Capabilities.FancyProgress)
        {
            ProgressConsole.WriteLine(line);
            return;
        }

        Console.Error.WriteLine(line);
    }

    /// <summary>
    /// Writes a heading to stderr, above the detail lines that belong to it.
    /// </summary>
    /// <param name="line">The heading text.</param>
    public void Heading(string line)
    {
        if (!Capabilities.ShowProgress)
        {
            return;
        }

        if (Capabilities.FancyProgress)
        {
            ProgressConsole.MarkupLine($"[{CliTheme.HEADER_STYLE}]{Markup.Escape(line)}[/]");
            return;
        }

        Console.Error.WriteLine(line);
    }

    /// <summary>
    /// Writes a supporting detail line to stderr, dimmed when the stream is a terminal.
    /// </summary>
    /// <param name="line">The detail text.</param>
    public void Detail(string line)
    {
        if (!Capabilities.ShowProgress)
        {
            return;
        }

        ErrorDetail(line);
    }

    /// <summary>
    /// Writes a supporting detail line that belongs to a failure, printed even when quiet.
    /// </summary>
    /// <param name="line">The detail text.</param>
    public void ErrorDetail(string line)
    {
        if (Capabilities.FancyProgress)
        {
            ProgressConsole.MarkupLine($"[{CliTheme.DETAIL_STYLE}]{Markup.Escape(line)}[/]");
            return;
        }

        Console.Error.WriteLine(line);
    }

    /// <summary>
    /// Writes a section heading above a block of results.
    /// </summary>
    /// <param name="title">The section title.</param>
    public void Section(string title)
    {
        if (IsJson)
        {
            return;
        }

        if (Capabilities.FancyResults)
        {
            Rule rule = new($"[{CliTheme.HEADER_STYLE}]{Markup.Escape(title)}[/]")
            {
                Justification = Justify.Left,
            };

            rule.RuleStyle(new Style(CliTheme.SECONDARY));
            ResultConsole.Write(rule);
            return;
        }

        _results.WriteLine();
        _results.WriteLine(title.ToUpperInvariant());
    }

    /// <summary>
    /// Writes a warning that deserves more attention than one line of stderr.
    /// </summary>
    /// <param name="title">The heading shown on the border.</param>
    /// <param name="lines">The warning text, one line per paragraph.</param>
    public void WarnPanel(string title, params string[] lines)
    {
        if (!Capabilities.FancyProgress)
        {
            Warn($"{title}: {lines.FirstOrDefault() ?? ""}");
            foreach (var line in lines.Skip(1))
            {
                ErrorDetail(line);
            }

            return;
        }

        Panel panel = new(string.Join('\n', lines.Select(Markup.Escape)))
        {
            Header = new PanelHeader($"[bold {CliTheme.WARNING.ToMarkup()}]{Markup.Escape(title)}[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0, 1, 0),
        };

        panel.BorderColor(CliTheme.WARNING);
        ProgressConsole.Write(panel);
    }

    /// <summary>
    /// Writes a warning to stderr.
    /// </summary>
    /// <param name="line">The warning text.</param>
    public void Warn(string line)
    {
        if (Capabilities.FancyProgress)
        {
            ProgressConsole.MarkupLine($"[bold {CliTheme.WARNING.ToMarkup()}]warning:[/] {Markup.Escape(line)}");
            return;
        }

        Console.Error.WriteLine($"warning: {line}");
    }

    /// <summary>
    /// Writes an error to stderr.
    /// </summary>
    /// <param name="line">The error text.</param>
    public void Error(string line)
    {
        if (Capabilities.FancyProgress)
        {
            ProgressConsole.MarkupLine($"[bold {CliTheme.BRAND_RED.ToMarkup()}]error:[/] {Markup.Escape(line)}");
            return;
        }

        Console.Error.WriteLine($"error: {line}");
    }

    /// <summary>
    /// Reports a failure and returns the exit code that describes it.
    /// </summary>
    /// <param name="exitCode">The exit code to return.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="details">Extra key and value pairs to print under the message, or null.</param>
    /// <returns>The value of <paramref name="exitCode" />.</returns>
    // In JSON mode the failure also goes to stdout, so a script parsing stdout sees the error rather
    // than an empty document.
    public int Fail(int exitCode, string message, IReadOnlyDictionary<string, string>? details = null)
    {
        Error(message);
        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                ErrorDetail($"  {key}: {value}");
            }
        }

        if (IsJson)
        {
            _results.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                exitCode,
                error = message,
                details,
            }, JSON_OPTIONS));
        }

        return exitCode;
    }

    /// <summary>
    /// Runs slow work behind a spinner, or silently when the stream cannot animate.
    /// </summary>
    /// <typeparam name="T">The type the work returns.</typeparam>
    /// <param name="label">The label shown next to the spinner.</param>
    /// <param name="work">The work to run, given a callback that replaces the label.</param>
    /// <returns>Whatever <paramref name="work" /> returned.</returns>
    public async Task<T> StatusAsync<T>(string label, Func<Action<string>, Task<T>> work)
    {
        if (!Capabilities.CanAnimate)
        {
            return await work(_ => { });
        }

        return await ProgressConsole
            .Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(CliTheme.BRAND_ORANGE))
            .StartAsync(label, status => work(update =>
            {
                status.Status = Markup.Escape(update);
                status.Refresh();
            }));
    }

    /// <summary>
    /// Writes rows to stdout, as a bordered table on a terminal and as aligned columns otherwise.
    /// </summary>
    /// <param name="headers">The column headers.</param>
    /// <param name="rows">The rows, each holding one cell per header.</param>
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<CliCell>> rows)
    {
        if (Capabilities.FancyResults)
        {
            WriteStyledTable(headers, rows);
            return;
        }

        WritePlainTable(headers, rows);
    }

    private void WriteStyledTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<CliCell>> rows)
    {
        Table table = new()
        {
            Border = TableBorder.Rounded,
        };

        table.BorderColor(CliTheme.SECONDARY);

        var pathWidth = PathColumnWidth(headers, rows);
        for (var column = 0; column < headers.Count; column++)
        {
            TableColumn definition = new($"[{CliTheme.HEADER_STYLE}]{Markup.Escape(headers[column])}[/]");
            definition.NoWrap();

            if (pathWidth > 0 && rows.Any(row => column < row.Count && row[column].IsPath))
            {
                definition.Width(pathWidth);
            }

            table.AddColumn(definition);
        }

        foreach (var row in rows)
        {
            List<IRenderable> cells = [];
            for (var column = 0; column < headers.Count; column++)
            {
                cells.Add(Render(column < row.Count ? row[column] : default));
            }

            table.AddRow(cells);
        }

        ResultConsole.Write(table);
    }

    // Every other column holds something that must be read in full, so the path columns are handed
    // whatever width is left over and shorten themselves to fit it.
    private int PathColumnWidth(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<CliCell>> rows)
    {
        List<int> pathColumns = [];
        var fixedWidth = 0;

        for (var column = 0; column < headers.Count; column++)
        {
            if (rows.Any(row => column < row.Count && row[column].IsPath))
            {
                pathColumns.Add(column);
                continue;
            }

            var widest = headers[column].Length;
            foreach (var row in rows)
            {
                widest = Math.Max(widest, column < row.Count ? row[column].Text?.Length ?? 0 : 0);
            }

            fixedWidth += widest;
        }

        if (pathColumns.Count == 0)
        {
            return 0;
        }

        // Two spaces of padding either side of every column, plus one border between and around.
        var chrome = (headers.Count * 3) + 1;
        var available = ResultConsole.Profile.Width - fixedWidth - chrome;

        return Math.Max(MINIMUM_PATH_WIDTH, available / pathColumns.Count);
    }

    /// <summary>
    /// Turns a cell into something Spectre can draw.
    /// </summary>
    /// <param name="cell">The cell to draw.</param>
    /// <returns>A path renderable for a path, otherwise the styled text.</returns>
    // A path is the one kind of cell worth shortening rather than wrapping, because the middle of
    // it is the least interesting part and the filename at the end is the most.
    public static IRenderable Render(CliCell cell)
    {
        var text = cell.Text ?? "";
        if (cell.IsPath && text.Length > 0)
        {
            TextPath path = new(text);
            path.RootStyle(new Style(CliTheme.SECONDARY));
            path.SeparatorStyle(new Style(CliTheme.SECONDARY));
            path.StemStyle(new Style(CliTheme.SECONDARY));
            path.LeafStyle(new Style(Color.Default, decoration: Decoration.Bold));
            return path;
        }

        var escaped = Markup.Escape(text);
        return new Markup(cell.Style is null ? escaped : $"[{cell.Style}]{escaped}[/]");
    }

    private void WritePlainTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<CliCell>> rows)
    {
        var widths = new int[headers.Count];
        for (var column = 0; column < headers.Count; column++)
        {
            widths[column] = headers[column].Length;
        }

        foreach (var row in rows)
        {
            for (var column = 0; column < headers.Count && column < row.Count; column++)
            {
                widths[column] = Math.Max(widths[column], row[column].Text?.Length ?? 0);
            }
        }

        _results.WriteLine(string.Join("  ", PadToWidths([.. headers.Select(CliCell.FromString)], widths)).TrimEnd());
        _results.WriteLine(string.Join("  ", DashesForWidths(widths)));
        foreach (var row in rows)
        {
            _results.WriteLine(string.Join("  ", PadToWidths(row, widths)).TrimEnd());
        }
    }

    private static IEnumerable<string> PadToWidths(IReadOnlyList<CliCell> cells, int[] widths)
    {
        for (var column = 0; column < widths.Length; column++)
        {
            var cell = column < cells.Count ? cells[column].Text ?? "" : "";
            yield return cell.PadRight(widths[column]);
        }
    }

    private static IEnumerable<string> DashesForWidths(int[] widths)
    {
        foreach (var width in widths)
        {
            yield return new string('-', width);
        }
    }
}
