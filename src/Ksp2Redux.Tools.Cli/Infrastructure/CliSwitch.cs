namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Reads the on and off values the settings options take.
/// </summary>
public static class CliSwitch
{
    /// <summary>
    /// Reads an on or off value: on, true, yes or 1, and off, false, no or 0, in any case.
    /// </summary>
    /// <param name="value">The value given, or null when the option was left out.</param>
    /// <param name="parsed">True for on, false for off, null when the option was left out.</param>
    /// <returns>False when a value was given that is neither on nor off.</returns>
    public static bool TryParse(string? value, out bool? parsed)
    {
        parsed = value?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "on" or "true" or "yes" or "1" => true,
            "off" or "false" or "no" or "0" => false,
            _ => null,
        };

        return value is null || parsed is not null;
    }
}
