using System.Threading.Tasks;
using Avalonia.Controls;
using Ksp2Redux.Tools.Launcher.Services;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Cli;

/// <summary>
/// Dialog service for a process with no window, writing the dialog text to stderr instead.
/// </summary>
// The launcher services the CLI reuses only raise dialogs on failure paths they already recover
// from, so the text is reported as a warning and the caller gets a default answer back. The failure
// itself still surfaces through the return value or the log.
public sealed class CliMessageBoxService : IMessageBoxService
{
    private readonly CliOutput _output;

    /// <summary>
    /// Initializes the service against the writer that receives the dialog text.
    /// </summary>
    /// <param name="output">The writer that receives dialog text as warnings.</param>
    public CliMessageBoxService(CliOutput output)
    {
        _output = output;
    }

    /// <inheritdoc />
    public Task<ButtonResult> ShowMessageBoxAsOwnedAsync(
        string title,
        string text,
        ButtonEnum @enum = ButtonEnum.Ok,
        Icon icon = Icon.None,
        object? context = null,
        WindowStartupLocation windowStartupLocation = WindowStartupLocation.CenterScreen)
    {
        _output.Warn($"{title}: {text}");
        return Task.FromResult(DefaultResultFor(@enum));
    }

    // Mirrors the button a user gets by dismissing the dialog, so a caller branching on the result
    // takes its non destructive path rather than reading Ok as consent it never got.
    private static ButtonResult DefaultResultFor(ButtonEnum buttons) => buttons switch
    {
        ButtonEnum.Ok => ButtonResult.Ok,
        ButtonEnum.OkCancel => ButtonResult.Cancel,
        ButtonEnum.YesNo => ButtonResult.No,
        ButtonEnum.YesNoCancel => ButtonResult.Cancel,
        ButtonEnum.YesNoAbort => ButtonResult.Abort,
        ButtonEnum.OkAbort => ButtonResult.Abort,
        _ => ButtonResult.None,
    };
}
