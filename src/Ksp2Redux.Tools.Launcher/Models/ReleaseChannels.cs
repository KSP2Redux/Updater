namespace Ksp2Redux.Tools.Launcher.Models;

public static class ReleaseChannels
{
    public const string STABLE = "stable";
    public const string SNAPSHOT = "beta";
    public const string SNAPSHOT_DISPLAY_NAME = "snapshot";

    public static string DisplayName(string? channel) =>
        string.Equals(channel, SNAPSHOT, StringComparison.OrdinalIgnoreCase)
            ? SNAPSHOT_DISPLAY_NAME
            : channel ?? string.Empty;

    public static string FromDisplayName(string channel) =>
        string.Equals(channel, SNAPSHOT_DISPLAY_NAME, StringComparison.OrdinalIgnoreCase)
            ? SNAPSHOT
            : channel;
}
