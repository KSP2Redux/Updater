using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Retargets the stock bundles' embedded shader copies to the Redux replacement shader bundles.
/// </summary>
public sealed class BundlesConvertCommand : ReduxCommand<BundlesConvertSettings>
{
    private const int SAMPLE_BUNDLES = 3;

    /// <inheritdoc />
    protected override async Task<int> RunAsync(
        CliContext context,
        BundlesConvertSettings settings,
        CancellationToken cancellationToken)
    {
        if (!BundleCommandSupport.TryResolveGame(context, settings, out var game, out var exitCode))
        {
            return exitCode;
        }

        if (string.IsNullOrWhiteSpace(settings.ShaderManifest))
        {
            return context.Output.Fail(ExitCode.USAGE_ERROR, "--shader-manifest is required: the manifest.json of the replacement shader bundles.");
        }

        var manifestPath = context.FileSystem.Path.GetFullPath(settings.ShaderManifest.Trim().Trim('"'));
        if (!context.FileSystem.File.Exists(manifestPath))
        {
            return context.Output.Fail(ExitCode.PATH_NOT_FOUND, $"The shader manifest does not exist: {manifestPath}");
        }

        var manifest = ShaderManifestIndex.Load(context.FileSystem, manifestPath);
        foreach (var warning in manifest.Warnings)
        {
            context.Output.Warn(warning);
        }

        var result = await BundleCommandSupport.RunAsync(
            context,
            game,
            settings.IsDryRun ? "Planning the bundle conversion" : "Converting bundles",
            converter => converter.Apply(manifest, settings.IsDryRun, cancellationToken));

        var plan = result.Plan;
        var retargeted = RetargetedByShader(plan);
        var skipped = SkippedByShader(plan);
        var fields = FieldReferenceGroups(plan);
        var succeeded = result.Failures.Count == 0 && result.ForeignEdits.Count == 0;

        context.Output.Payload(
            new
            {
                ok = succeeded,
                dryRun = result.DryRun,
                game,
                manifest = manifestPath,
                manifestSha256 = manifest.ManifestSha256,
                journal = result.JournalPath,
                bundlesScanned = plan.BundlesScanned,
                unreadable = Problems(plan.Errors),
                warnings = Problems(plan.Warnings),
                inPlace = Totals(plan, BundleEditMode.InPlace),
                rewrite = Totals(plan, BundleEditMode.Rewrite),
                sameSizeBundles = plan.Bundles.Count(bundle => bundle.IsSameSize),
                newExternalBundles = plan.Bundles.Count(bundle => !bundle.IsSameSize),
                materialsOnReplacement = plan.MaterialsOnReplacement,
                retargeted = retargeted.Select(group => new
                {
                    shader = group.Shader,
                    materials = group.Materials,
                    otherFields = group.OtherFields,
                    bundles = group.Bundles,
                    bundlesInPlace = group.BundlesInPlace,
                    bundlesRewritten = group.BundlesRewritten,
                }),
                skipped = skipped.Select(group => new
                {
                    shader = group.Shader,
                    references = group.References,
                    bundles = group.Bundles,
                    reasons = group.Reasons,
                }),
                shaderFields = fields.Select(group => new
                {
                    kind = group.Kind,
                    script = group.Script,
                    field = group.Field,
                    count = group.Count,
                    retargeted = group.Retargeted,
                    shaders = group.Shaders,
                    outcomes = group.Outcomes,
                    bundles = group.Bundles,
                }),
                applied = result.DryRun
                    ? null
                    : new
                    {
                        editsWritten = result.EditsWritten,
                        editsAlreadyApplied = result.EditsAlreadyApplied,
                        editsUpdated = result.EditsUpdated,
                        editsReverted = result.EditsReverted,
                        copiesWritten = result.CopiesWritten,
                        copiesUpToDate = result.CopiesUpToDate,
                        copiesDeleted = result.CopiesDeleted,
                        foreignEdits = result.ForeignEdits.Select(edit => new { file = edit.File, offset = edit.Offset }),
                        failures = Problems(result.Failures),
                        droppedEntries = Problems(result.DroppedEntries),
                    },
                bundles = plan.Bundles.Select(bundle => new
                {
                    file = bundle.File,
                    mode = bundle.Mode.ToString(),
                    reason = bundle.RewriteReason,
                    edits = bundle.Edits.Count,
                }),
            },
            () => WriteText(context, result, retargeted, skipped, fields));

        foreach (var foreign in result.ForeignEdits)
        {
            context.Output.Warn($"{foreign.File} at {foreign.Offset} holds neither the stock nor the converted bytes and was left alone.");
        }

        foreach (var failure in result.Failures)
        {
            context.Output.Error($"{failure.File}: {failure.Message}");
        }

        foreach (var dropped in result.DroppedEntries)
        {
            context.Output.Warn($"{dropped.File}: {dropped.Message}");
        }

        return succeeded ? ExitCode.SUCCESS : ExitCode.BUNDLE_CONVERSION_FAILED;
    }

    private static IEnumerable<object> Problems(IEnumerable<BundleProblem> problems) =>
        problems.Select(problem => new { file = problem.File, message = problem.Message });

    private static object Totals(ConversionPlan plan, BundleEditMode mode)
    {
        var bundles = plan.Bundles.Where(bundle => bundle.Mode == mode).ToList();
        return new
        {
            bundles = bundles.Count,
            bytes = bundles.Sum(bundle => bundle.Size),
            materials = plan.CountEdits(mode, ShaderReferenceKind.Material),
            monoBehaviours = plan.CountEdits(mode, ShaderReferenceKind.MonoBehaviour),
            otherFields = plan.CountEdits(mode, ShaderReferenceKind.Other),
            preloadEntries = plan.CountEdits(mode, ShaderReferenceKind.Preload),
            reasons = bundles
                .Where(bundle => bundle.RewriteReason is not null)
                .GroupBy(bundle => bundle.RewriteReason!.StartsWith("no external", StringComparison.Ordinal) ? "no external for the target CAB" : bundle.RewriteReason!)
                .ToDictionary(group => group.Key, group => group.Count()),
        };
    }

    private static List<RetargetedGroup> RetargetedByShader(ConversionPlan plan) =>
    [
        .. plan.Bundles
            .SelectMany(bundle => bundle.Edits
                .Where(edit => edit.Kind != ShaderReferenceKind.Preload)
                .Select(edit => (Bundle: bundle, Edit: edit)))
            .GroupBy(pair => pair.Edit.ShaderName, StringComparer.Ordinal)
            .Select(group =>
            {
                var bundles = group.Select(pair => pair.Bundle).Distinct().ToList();
                return new RetargetedGroup(
                    group.Key,
                    group.Count(pair => pair.Edit.Kind == ShaderReferenceKind.Material),
                    group.Count(pair => pair.Edit.Kind != ShaderReferenceKind.Material),
                    bundles.Count,
                    bundles.Count(bundle => bundle.Mode == BundleEditMode.InPlace),
                    bundles.Count(bundle => bundle.Mode == BundleEditMode.Rewrite));
            })
            .OrderByDescending(group => group.Materials + group.OtherFields)
            .ThenBy(group => group.Shader, StringComparer.Ordinal),
    ];

    private static List<SkippedGroup> SkippedByShader(ConversionPlan plan) =>
    [
        .. plan.Skipped
            .GroupBy(skip => skip.Shader, StringComparer.Ordinal)
            .Select(group => new SkippedGroup(
                group.Key,
                group.Count(),
                group.Select(skip => skip.File).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                [.. group.Select(skip => skip.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderByDescending(group => group.References)
            .ThenBy(group => group.Shader, StringComparer.Ordinal),
    ];

    private static List<FieldGroup> FieldReferenceGroups(ConversionPlan plan) =>
    [
        .. plan.FieldReferences
            .GroupBy(reference => (reference.Kind, reference.Script, reference.FieldPath))
            .Select(group => new FieldGroup(
                group.Key.Kind.ToString(),
                group.Key.Script,
                group.Key.FieldPath,
                group.Count(),
                group.Count(reference => reference.Outcome == "retargeted"),
                [.. group.Select(reference => reference.Shader).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. group.Select(reference => reference.Outcome).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. group.Select(reference => reference.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)]))
            .OrderBy(group => group.Script, StringComparer.Ordinal)
            .ThenBy(group => group.Field, StringComparer.Ordinal),
    ];

    private static void WriteText(
        CliContext context,
        ConversionResult result,
        List<RetargetedGroup> retargeted,
        List<SkippedGroup> skipped,
        List<FieldGroup> fields)
    {
        var plan = result.Plan;
        var output = context.Output;
        var inPlace = plan.Bundles.Where(bundle => bundle.Mode == BundleEditMode.InPlace).ToList();
        var rewrite = plan.Bundles.Where(bundle => bundle.Mode == BundleEditMode.Rewrite).ToList();

        output.Result(result.DryRun ? "Plan (dry run, nothing written)" : "Conversion");
        output.Result($"  bundles scanned            {plan.BundlesScanned}, unreadable {plan.Errors.Count}");
        output.Result($"  bundles edited in place    {inPlace.Count} ({CliFormat.Bytes(inPlace.Sum(bundle => bundle.Size))})");
        output.Result($"  bundles rewritten          {rewrite.Count} ({CliFormat.Bytes(rewrite.Sum(bundle => bundle.Size))} stock)");
        output.Result($"    edits fit the file as is {plan.Bundles.Count(bundle => bundle.IsSameSize)}, need a new external {plan.Bundles.Count(bundle => !bundle.IsSameSize)}");
        output.Result($"  materials retargeted       {plan.CountEdits(BundleEditMode.InPlace, ShaderReferenceKind.Material)} in place, {plan.CountEdits(BundleEditMode.Rewrite, ShaderReferenceKind.Material)} via rewrite");
        output.Result($"  MonoBehaviour fields       {plan.CountEdits(BundleEditMode.InPlace, ShaderReferenceKind.MonoBehaviour)} in place, {plan.CountEdits(BundleEditMode.Rewrite, ShaderReferenceKind.MonoBehaviour)} via rewrite");
        output.Result($"  preload entries            {plan.CountEdits(BundleEditMode.InPlace, ShaderReferenceKind.Preload)} in place, {plan.CountEdits(BundleEditMode.Rewrite, ShaderReferenceKind.Preload)} via rewrite");
        output.Result($"  materials already on a replacement bundle {plan.MaterialsOnReplacement}");

        if (!result.DryRun)
        {
            output.Result($"  edits written {result.EditsWritten}, already applied {result.EditsAlreadyApplied}, updated {result.EditsUpdated}, reverted {result.EditsReverted}");
            output.Result($"  copies written {result.CopiesWritten}, up to date {result.CopiesUpToDate}, deleted {result.CopiesDeleted}");
            output.Result($"  journal {result.JournalPath}");
        }

        foreach (var error in plan.Errors)
        {
            output.Warn($"{error.File} could not be read: {error.Message}");
        }

        if (plan.Warnings.Count > 0)
        {
            output.Warn($"{plan.Warnings.Count} objects could not be read. Run with --json for the list.");
        }

        if (retargeted.Count > 0)
        {
            output.Section($"Retargeted shaders ({retargeted.Count})");
            output.Table(
                ["Shader", "Materials", "Other fields", "Bundles", "In place", "Rewritten"],
                [
                    .. retargeted.Select(group => (IReadOnlyList<CliCell>)
                    [
                        group.Shader,
                        group.Materials.ToString(),
                        group.OtherFields.ToString(),
                        group.Bundles.ToString(),
                        group.BundlesInPlace.ToString(),
                        group.BundlesRewritten.ToString(),
                    ]),
                ]);
        }

        if (skipped.Count > 0)
        {
            output.Section($"Skipped shaders ({skipped.Sum(group => group.References)} references)");
            output.Table(
                ["Shader", "Refs", "Bundles", "Reason"],
                [.. skipped.Select(group => (IReadOnlyList<CliCell>)[group.Shader, group.References.ToString(), group.Bundles.ToString(), string.Join("; ", group.Reasons)])]);
        }

        output.Section($"Shader fields outside materials ({fields.Sum(group => group.Count)} found)");
        if (fields.Count == 0)
        {
            output.Result("  none");
            return;
        }

        output.Table(
            ["Kind", "Script", "Field", "Refs", "Retargeted", "Outcome", "Bundles"],
            [
                .. fields.Select(group => (IReadOnlyList<CliCell>)
                [
                    group.Kind,
                    group.Script,
                    group.Field,
                    group.Count.ToString(),
                    group.Retargeted.ToString(),
                    string.Join("; ", group.Outcomes),
                    Sample(group.Bundles),
                ]),
            ]);
    }

    private static string Sample(IReadOnlyList<string> bundles) =>
        bundles.Count <= SAMPLE_BUNDLES
            ? string.Join(", ", bundles)
            : $"{string.Join(", ", bundles.Take(SAMPLE_BUNDLES))} and {bundles.Count - SAMPLE_BUNDLES} more";

    /// <summary>
    /// The retargeted references to one shader.
    /// </summary>
    private sealed record RetargetedGroup(string Shader, int Materials, int OtherFields, int Bundles, int BundlesInPlace, int BundlesRewritten);

    /// <summary>
    /// The skipped references to one shader.
    /// </summary>
    private sealed record SkippedGroup(string Shader, int References, int Bundles, IReadOnlyList<string> Reasons);

    /// <summary>
    /// The shader references in one field of one script.
    /// </summary>
    private sealed record FieldGroup(
        string Kind,
        string Script,
        string Field,
        int Count,
        int Retargeted,
        IReadOnlyList<string> Shaders,
        IReadOnlyList<string> Outcomes,
        IReadOnlyList<string> Bundles);
}
