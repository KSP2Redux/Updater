namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Shared formatting for values that show up in more than one command.
/// </summary>
public static class CliFormat
{
    private static readonly string[] UNITS = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Renders a byte count in the largest unit that keeps it readable.
    /// </summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <returns>A string such as "1.4 GB".</returns>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < UNITS.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {UNITS[unit]}" : $"{value:F1} {UNITS[unit]}";
    }
}
