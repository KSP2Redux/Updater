using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Checks a stock bundle conversion against the files on disk.
/// </summary>
public sealed class BundlesVerifyCommand : ReduxCommand<BundlesVerifySettings>
{
    private const int LISTED_PROBLEMS = 20;

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        BundlesVerifySettings settings,
        CancellationToken cancellationToken)
    {
        if (!BundleCommandSupport.TryResolveGame(context, settings, out var game, out var exitCode))
        {
            return exitCode;
        }

        var result = await BundleCommandSupport.RunAsync(
            context,
            game,
            "Checking the bundle conversion",
            converter => converter.Verify(cancellationToken));

        var badEdits = result.Edits.Where(edit => edit.State != EditState.Applied).ToList();
        var badOutputs = result.Outputs.Where(output => output.State != OutputState.Ok).ToList();

        context.Output.Payload(
            new
            {
                ok = result.IsComplete,
                game,
                hasJournal = result.HasJournal,
                manifestSha256 = result.ManifestSha256,
                edits = new
                {
                    applied = result.Count(EditState.Applied),
                    stock = result.Count(EditState.Stock),
                    foreign = result.Count(EditState.Foreign),
                    fileChanged = result.Count(EditState.FileChanged),
                },
                copies = new
                {
                    ok = result.Count(OutputState.Ok),
                    missing = result.Count(OutputState.Missing),
                    hashMismatch = result.Count(OutputState.HashMismatch),
                    pending = result.Count(OutputState.Pending),
                    stockChanged = result.Count(OutputState.StockChanged),
                    invalid = result.Count(OutputState.Invalid),
                },
                problems = badEdits.Select(edit => new { file = edit.File, offset = (long?)edit.Offset, output = (string?)null, state = edit.State.ToString() })
                    .Concat(badOutputs.Select(output => new { file = output.File, offset = (long?)null, output = (string?)output.Output, state = output.State.ToString() })),
            },
            () =>
            {
                if (!result.HasJournal)
                {
                    context.Output.Result("Not converted: no conversion journal.");
                    return;
                }

                context.Output.Result(result.IsComplete ? "Conversion complete" : "Conversion incomplete");
                context.Output.Result($"  edits   applied {result.Count(EditState.Applied)}, stock (needs reapply) {result.Count(EditState.Stock)}, foreign {result.Count(EditState.Foreign)}, file changed {result.Count(EditState.FileChanged)}");
                context.Output.Result($"  copies  ok {result.Count(OutputState.Ok)}, missing {result.Count(OutputState.Missing)}, hash mismatch {result.Count(OutputState.HashMismatch)}, pending {result.Count(OutputState.Pending)}, stock changed {result.Count(OutputState.StockChanged)}, invalid {result.Count(OutputState.Invalid)}");

                foreach (var edit in badEdits.Take(LISTED_PROBLEMS))
                {
                    context.Output.Result($"  {edit.State,-12} {edit.File} @ {edit.Offset}");
                }

                foreach (var output in badOutputs.Take(LISTED_PROBLEMS))
                {
                    context.Output.Result($"  {output.State,-12} {output.Output}");
                }

                var unlisted = Math.Max(0, badEdits.Count - LISTED_PROBLEMS) + Math.Max(0, badOutputs.Count - LISTED_PROBLEMS);
                if (unlisted > 0)
                {
                    context.Output.Result($"  and {unlisted} more, run with --json for the full list");
                }
            });

        return result.IsComplete ? ExitCode.SUCCESS : ExitCode.BUNDLES_NOT_CONVERTED;
    }
}
