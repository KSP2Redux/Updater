namespace Ksp2Redux.Tools.Launcher.Localization;

/// <summary>
/// A line of text that goes to the log file in English and to the screen in the user's language.
/// There is deliberately no conversion from or to string, so the compiler lists every place that still passes plain text.
/// </summary>
/// <param name="English">What the log file gets.</param>
/// <param name="Localized">What the user sees.</param>
public readonly record struct LogMessage(string English, string Localized)
{
    /// <summary>Text that is never translated, such as process output or the message of a framework exception.</summary>
    public static LogMessage Literal(string text) => new(text, text);

    /// <summary>Always the English text, so an accidental interpolation cannot put a translation in a log.</summary>
    public override string ToString() => English;

    /// <summary>Creates an exception with the English text as its message and the localized text in its Data.</summary>
    public T ToException<T>(Func<string, T> create) where T : Exception
    {
        var exception = create(English);
        exception.Data[LogMessageExceptionExtensions.LOCALIZED_KEY] = Localized;
        return exception;
    }
}

public static class LogMessageExceptionExtensions
{
    internal const string LOCALIZED_KEY = "Localized";

    /// <summary>The text to show the user: the localized one for exceptions the launcher raised, else the message.</summary>
    public static string UserMessage(this Exception exception) =>
        exception.Data[LOCALIZED_KEY] is string localized && localized.Length > 0 ? localized : exception.Message;

    /// <summary>Lets a wrapper sentence nest the exception's text on both sides.</summary>
    public static LogMessage ToLogMessage(this Exception exception) => new(exception.Message, exception.UserMessage());
}
