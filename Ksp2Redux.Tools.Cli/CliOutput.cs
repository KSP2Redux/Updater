using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Cli;

/// <summary>
/// Writer for everything the verbs print.
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

    private readonly TextWriter _results;

    /// <summary>
    /// Initializes a writer over the stream that carries results, in either text or JSON mode.
    /// </summary>
    /// <param name="results">The stream results are written to, normally the process's real stdout.</param>
    /// <param name="isJson">True to emit a JSON document, false for human readable text.</param>
    public CliOutput(TextWriter results, bool isJson)
    {
        _results = results;
        IsJson = isJson;
    }

    /// <summary>
    /// Gets a value indicating whether the result stream carries a JSON document rather than text.
    /// </summary>
    public bool IsJson { get; }

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
        Console.Error.WriteLine(line);
    }

    /// <summary>
    /// Writes a warning to stderr.
    /// </summary>
    /// <param name="line">The warning text.</param>
    public void Warn(string line)
    {
        Console.Error.WriteLine($"warning: {line}");
    }

    /// <summary>
    /// Writes an error to stderr.
    /// </summary>
    /// <param name="line">The error text.</param>
    public void Error(string line)
    {
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
                Console.Error.WriteLine($"  {key}: {value}");
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
    /// Writes rows to stdout as aligned columns.
    /// </summary>
    /// <param name="headers">The column headers.</param>
    /// <param name="rows">The rows, each holding one cell per header.</param>
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
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
                widths[column] = Math.Max(widths[column], row[column]?.Length ?? 0);
            }
        }

        _results.WriteLine(string.Join("  ", PadToWidths(headers, widths)).TrimEnd());
        _results.WriteLine(string.Join("  ", DashesForWidths(widths)));
        foreach (var row in rows)
        {
            _results.WriteLine(string.Join("  ", PadToWidths(row, widths)).TrimEnd());
        }
    }

    private static IEnumerable<string> PadToWidths(IReadOnlyList<string> cells, int[] widths)
    {
        for (var column = 0; column < widths.Length; column++)
        {
            var cell = column < cells.Count ? cells[column] ?? "" : "";
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
