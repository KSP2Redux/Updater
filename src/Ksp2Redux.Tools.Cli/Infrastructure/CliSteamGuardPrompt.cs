using Ksp2Redux.Tools.Launcher.Services.Steam;
using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Answers Steam Guard during a password sign-in by asking on the terminal.
/// </summary>
/// <param name="output">The writer whose terminal the questions are asked on.</param>
public sealed class CliSteamGuardPrompt(CliOutput output) : ISteamGuardPrompt
{
    /// <inheritdoc />
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        Task.FromResult(AskCode(previousCodeWasIncorrect
            ? "That code was wrong. Enter the new Steam Guard code sent to"
            : "Enter the Steam Guard code sent to", email));

    /// <inheritdoc />
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        Task.FromResult(AskCode(previousCodeWasIncorrect
            ? "That code was wrong. Enter the current code from"
            : "Enter the code from", "the Steam Mobile App"));

    /// <inheritdoc />
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        output.Heading("Approve the sign-in in the Steam Mobile App. Waiting for it...");
        return Task.FromResult(true);
    }

    // Steam Guard codes are five letters and digits and are case-insensitive, but Steam expects them
    // upper case.
    private string AskCode(string question, string where)
    {
        TextPrompt<string> prompt = new($"{Markup.Escape(question)} [bold]{Markup.Escape(where)}[/]:");
        prompt.Validate(code => code.Trim().Length == 5
            ? ValidationResult.Success()
            : ValidationResult.Error("A Steam Guard code is 5 characters."));
        return output.ProgressConsole.Prompt(prompt).Trim().ToUpperInvariant();
    }
}
