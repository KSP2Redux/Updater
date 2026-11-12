using System.ComponentModel;
using System.Globalization;

namespace Ksp2Redux.Tools.Launcher.Localization;

/// <summary>A language the launcher can be switched to, named in that language.</summary>
/// <param name="CultureName">The culture name, for example "pt-BR".</param>
/// <param name="DisplayName">The language's own name with a capital first letter.</param>
public sealed record LanguageOption(string CultureName, string DisplayName);

public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>Gets the text for a key, or the key itself when there is none.</summary>
    string this[string key] { get; }

    /// <summary>Gets the text for a key with the arguments filled in. Numbers follow the OS culture.</summary>
    string Format(string key, params object?[] args);

    /// <summary>
    /// Gets the text for a key with the arguments filled in, once in English for the log file and once in the
    /// current language for the screen. A <see cref="LogMessage"/> argument expands per side.
    /// </summary>
    LogMessage Message(string key, params object?[] args);

    /// <summary>The culture texts are currently looked up in.</summary>
    CultureInfo Culture { get; }

    /// <summary>English plus every language that has a translation file in the build.</summary>
    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>
    /// Switches the language. An empty or unknown name means the language of the operating system.
    /// </summary>
    void SetLanguage(string? cultureName);

    /// <summary>Raised after the language changed, once every lookup returns the new texts.</summary>
    event EventHandler? LanguageChanged;
}
