using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The platforms the CLI is built for, each with its own release asset.
/// </summary>
public enum CliPlatform
{
    /// <summary>Windows x64.</summary>
    Windows = 0,

    /// <summary>Linux x64.</summary>
    Linux = 1,

    /// <summary>macOS on Apple Silicon.</summary>
    MacOS = 2,
}

/// <summary>
/// Works out which <see cref="CliPlatform" /> the CLI is running on.
/// </summary>
public static class CliPlatforms
{
    /// <summary>
    /// Gets the platform the operating system service reports.
    /// </summary>
    /// <param name="operatingSystem">The service answering which platform this is.</param>
    /// <returns>The running platform, Windows when it is neither Linux nor macOS.</returns>
    public static CliPlatform Current(IOperatingSystemService operatingSystem) =>
        operatingSystem.IsLinux() ? CliPlatform.Linux
        : operatingSystem.IsMacOS() ? CliPlatform.MacOS
        : CliPlatform.Windows;
}
