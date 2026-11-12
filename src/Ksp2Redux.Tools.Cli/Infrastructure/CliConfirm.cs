using Spectre.Console;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The answer to a confirmation question.
/// </summary>
public enum ConfirmAnswer
{
    /// <summary>
    /// The command may go ahead.
    /// </summary>
    Approved = 0,

    /// <summary>
    /// The user said no.
    /// </summary>
    Declined = 1,

    /// <summary>
    /// Nobody could be asked and the command is one that refuses to guess.
    /// </summary>
    NeedsFlag = 2,
}

/// <summary>
/// Asks before something destructive happens.
/// </summary>
public static class CliConfirm
{
    /// <summary>
    /// Asks the user to confirm, or answers on their behalf when there is no terminal to ask on.
    /// </summary>
    /// <param name="output">The writer the question is asked through.</param>
    /// <param name="assumeYes">True when the caller passed the flag that skips the question.</param>
    /// <param name="question">The question, phrased so that yes means go ahead.</param>
    /// <param name="requireAnswer">
    /// True for a command that refuses to run unattended without the flag, false for one that has
    /// always run unattended and must keep doing so.
    /// </param>
    /// <returns>What the command should do.</returns>
    public static ConfirmAnswer Ask(CliOutput output, bool assumeYes, string question, bool requireAnswer)
    {
        if (assumeYes) return ConfirmAnswer.Approved;

        if (output.Capabilities.CanPrompt)
        {
            ConfirmationPrompt prompt = new(Markup.Escape(question))
            {
                DefaultValue = false,
            };

            return output.ProgressConsole.Prompt(prompt) ? ConfirmAnswer.Approved : ConfirmAnswer.Declined;
        }

        return requireAnswer ? ConfirmAnswer.NeedsFlag : ConfirmAnswer.Approved;
    }
}
