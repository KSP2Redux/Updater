using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Undoes a stock bundle conversion from its journal.
/// </summary>
public sealed class BundlesRevertCommand : ReduxCommand<BundlesRevertSettings>
{
    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        BundlesRevertSettings settings,
        CancellationToken cancellationToken)
    {
        if (!BundleCommandSupport.TryResolveGame(context, settings, out var game, out var exitCode))
        {
            return exitCode;
        }

        var result = await BundleCommandSupport.RunAsync(
            context,
            game,
            "Reverting the bundle conversion",
            converter => converter.Revert(cancellationToken));

        var succeeded = result.Failures.Count == 0 && result.Unreverted.Count == 0;
        context.Output.Payload(
            new
            {
                ok = succeeded,
                game,
                hadJournal = result.HadJournal,
                editsReverted = result.EditsReverted,
                editsAlreadyStock = result.EditsAlreadyStock,
                unreverted = result.Unreverted.Select(edit => new { file = edit.File, offset = edit.Offset, state = edit.State.ToString() }),
                copiesDeleted = result.CopiesDeleted,
                journalDeleted = result.JournalDeleted,
                failures = result.Failures.Select(failure => new { file = failure.File, message = failure.Message }),
            },
            () =>
            {
                if (!result.HadJournal)
                {
                    context.Output.Result("No conversion journal, nothing to revert.");
                    return;
                }

                context.Output.Result($"edits reverted {result.EditsReverted}, already stock {result.EditsAlreadyStock}, left alone {result.Unreverted.Count}");
                context.Output.Result($"copies deleted {result.CopiesDeleted}");
                context.Output.Result(result.JournalDeleted ? "journal deleted" : "journal kept, run revert again once the problems below are fixed");
            });

        foreach (var edit in result.Unreverted)
        {
            context.Output.Warn(edit.State == EditState.FileChanged
                ? $"{edit.File} is missing or changed size, the edit at {edit.Offset} was left alone."
                : $"{edit.File} at {edit.Offset} no longer holds the converted bytes and was left alone.");
        }

        foreach (var failure in result.Failures)
        {
            context.Output.Error($"{failure.File}: {failure.Message}");
        }

        return succeeded ? ExitCode.SUCCESS : ExitCode.BUNDLE_CONVERSION_FAILED;
    }
}
