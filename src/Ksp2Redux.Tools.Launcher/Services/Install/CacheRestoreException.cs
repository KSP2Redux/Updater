using Ksp2Redux.Tools.Launcher.Localization;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Thrown by <see cref="CacheService.RecursivelyRestoreCache"/> when the pre-patch snapshot it needs
/// to restore from is missing, carrying the directory and expected file so the failure is
/// distinguishable from other exceptions in a log without inspecting the message text.
/// </summary>
public sealed class CacheRestoreException : Exception
{
    public CacheRestoreException(string directory, string expectedFile)
        : this(LocalizationService.Default.Message("Cache.StockDeleted", expectedFile, directory), directory, expectedFile)
    {
    }

    // The message is English for the log, and the user's language rides along in Data for UserMessage().
    private CacheRestoreException(LogMessage message, string directory, string expectedFile)
        : base(message.English)
    {
        Data[LogMessageExceptionExtensions.LOCALIZED_KEY] = message.Localized;
        Directory = directory;
        ExpectedFile = expectedFile;
    }

    public string Directory { get; }
    public string ExpectedFile { get; }
}
