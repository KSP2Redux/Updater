using System.Text.RegularExpressions;
using Avalonia.Logging;

namespace Ksp2Redux.Tools.Launcher.Services.Infrastructure;

/// <summary>
/// Forwards Avalonia's own warnings and errors, such as a graphics backend failing to start, into the launcher log.
/// Anything logged before the launcher's services exist goes to the bootstrap log instead.
/// </summary>
internal sealed partial class AvaloniaLogSink : ILogSink
{
    public static ILogService? Target { get; set; }

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Error
        || (level == LogEventLevel.Warning && area != LogArea.Binding && area != LogArea.Property);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsEnabled(level, area)) Write(level, area, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (IsEnabled(level, area)) Write(level, area, Format(messageTemplate, propertyValues));
    }

    internal static string Format(string messageTemplate, object?[] propertyValues)
    {
        var index = 0;
        return Placeholder().Replace(messageTemplate, match =>
            index < propertyValues.Length ? propertyValues[index++]?.ToString() ?? "null" : match.Value);
    }

    private static void Write(LogEventLevel level, string area, string message)
    {
        var line = $"[Avalonia {area}] {message}";
        if (Target is not { } log)
        {
            LogService.WriteEarly(line);
        }
        else if (level >= LogEventLevel.Error)
        {
            log.Error(line);
        }
        else
        {
            log.Warn(line);
        }
    }

    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex Placeholder();
}
