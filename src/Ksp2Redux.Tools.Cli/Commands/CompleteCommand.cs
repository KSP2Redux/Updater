using Ksp2Redux.Tools.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Answers a completion script with the candidates for a partly typed command line.
/// </summary>
public sealed class CompleteCommand : Command<CompleteCommand.CompleteSettings>
{
    /// <summary>
    /// The prefix the completion scripts put on the word the cursor is sitting on.
    /// </summary>
    // The word travels as "word:<value>" rather than as an option, because an option value cannot
    // be empty and cannot start with a dash, and the word being completed is regularly both.
    public const string WORD_PREFIX = "word:";

    /// <summary>
    /// Settings for the hidden completion callback, which takes everything after a double dash.
    /// </summary>
    public sealed class CompleteSettings : CommandSettings;

    /// <inheritdoc />
    // This runs on every keypress a user spends on TAB, so it builds no container, writes no log
    // file and never fails loudly. No candidates is a perfectly good answer.
    protected override int Execute(CommandContext context, CompleteSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            string[] raw = [.. context.Remaining.Raw];
            var hasWord = raw.Length > 0 && raw[0].StartsWith(WORD_PREFIX, StringComparison.Ordinal);

            var word = hasWord ? raw[0][WORD_PREFIX.Length..] : "";
            var typed = hasWord ? raw[1..] : raw;

            foreach (var candidate in CliCommandCatalog.Candidates(typed, word))
            {
                Console.Out.WriteLine(candidate);
            }
        }
        catch (Exception)
        {
            return ExitCode.SUCCESS;
        }

        return ExitCode.SUCCESS;
    }
}
