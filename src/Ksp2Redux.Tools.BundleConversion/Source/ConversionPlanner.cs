using Ksp2Redux.Tools.BundleConversion.Bundles;

namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// Turns scanned bundles and a shader manifest into per-bundle edit plans.
/// </summary>
internal static class ConversionPlanner
{
    private const string NOT_IN_MANIFEST = "not in manifest";

    /// <summary>
    /// Plans the retargets for a set of scanned bundles.
    /// </summary>
    /// <param name="bundles">Every scanned bundle. Cross-bundle shader references resolve only within this set.</param>
    /// <param name="manifest">The replacement shader manifest.</param>
    /// <returns>The plan.</returns>
    public static ConversionPlan Plan(IReadOnlyList<ScannedBundle> bundles, ShaderManifestIndex manifest)
    {
        Dictionary<string, ScannedSerializedFile> files = new(StringComparer.OrdinalIgnoreCase);
        List<BundleProblem> errors = [];
        List<BundleProblem> warnings = [];
        foreach (var bundle in bundles)
        {
            if (bundle.Error is not null)
            {
                errors.Add(new BundleProblem(bundle.File, bundle.Error));
                continue;
            }

            foreach (var file in bundle.Files)
            {
                if (!files.ContainsKey(file.Name))
                {
                    files.Add(file.Name, file);
                }
                else
                {
                    warnings.Add(new BundleProblem(bundle.File, $"{file.Name} also appears in another bundle, the first one is used"));
                }

                warnings.AddRange(file.Warnings.Select(warning => new BundleProblem(bundle.File, $"{file.Name}: {warning}")));
            }
        }

        List<BundleEditPlan> plans = [];
        List<SkippedShaderReference> skipped = [];
        List<ShaderFieldReference> fieldReferences = [];
        var materialsOnReplacement = 0;

        foreach (var bundle in bundles.Where(bundle => bundle.Error is null).OrderBy(bundle => bundle.File, StringComparer.Ordinal))
        {
            BundlePlanner planner = new(bundle, files, manifest);
            planner.Run();

            skipped.AddRange(planner.Skipped);
            fieldReferences.AddRange(planner.FieldReferences);
            materialsOnReplacement += planner.MaterialsOnReplacement;
            if (planner.Plan is not null)
            {
                plans.Add(planner.Plan);
            }
        }

        return new ConversionPlan(
            manifest.ManifestSha256,
            bundles.Count,
            plans,
            skipped,
            fieldReferences,
            errors,
            warnings,
            materialsOnReplacement);
    }

    /// <summary>
    /// Plans one bundle.
    /// </summary>
    private sealed class BundlePlanner
    {
        private readonly ScannedBundle _bundle;
        private readonly IReadOnlyDictionary<string, ScannedSerializedFile> _files;
        private readonly ShaderManifestIndex _manifest;
        private readonly List<PlannedEdit> _edits = [];
        private readonly Dictionary<string, List<string>> _addedExternals = new(StringComparer.Ordinal);

        // Keyed by the shader's own CAB and path ID, so preload entries in any serialized file of the
        // bundle can find the retarget whatever file ID they use to reach it.
        private readonly Dictionary<(string Cab, long PathId), (ShaderTarget Target, string Name)> _retargets = [];

        public BundlePlanner(ScannedBundle bundle, IReadOnlyDictionary<string, ScannedSerializedFile> files, ShaderManifestIndex manifest)
        {
            _bundle = bundle;
            _files = files;
            _manifest = manifest;
        }

        public List<SkippedShaderReference> Skipped { get; } = [];

        public List<ShaderFieldReference> FieldReferences { get; } = [];

        public int MaterialsOnReplacement { get; private set; }

        public BundleEditPlan? Plan { get; private set; }

        public void Run()
        {
            foreach (var file in _bundle.Files)
            {
                foreach (var field in file.ShaderFields)
                {
                    PlanField(file, field);
                }
            }

            foreach (var file in _bundle.Files)
            {
                foreach (var preload in file.Preloads)
                {
                    PlanPreload(file, preload);
                }
            }

            if (_edits.Count == 0)
            {
                return;
            }

            Dictionary<string, IReadOnlyList<string>> added = new(StringComparer.Ordinal);
            foreach (var pair in _addedExternals)
            {
                added.Add(pair.Key, pair.Value);
            }

            var ordered = _edits.OrderBy(edit => edit.DataOffset).ToList();
            Plan = new BundleEditPlan(_bundle.File, _bundle.Size, ordered, added);
            ChooseMode(Plan);
        }

        private void ChooseMode(BundleEditPlan plan)
        {
            if (plan.AddedExternals.Count > 0)
            {
                var missing = plan.AddedExternals.SelectMany(pair => pair.Value).Distinct(StringComparer.Ordinal);
                plan.Mode = BundleEditMode.Rewrite;
                plan.RewriteReason = $"no external for {string.Join(", ", missing)}";
                return;
            }

            var layout = _bundle.Layout!;
            foreach (var edit in plan.Edits)
            {
                if (!layout.TryMapUncompressed(edit.DataOffset, Hex.PPTR_SIZE, out _))
                {
                    plan.Mode = BundleEditMode.Rewrite;
                    plan.RewriteReason = "edits fall in compressed blocks";
                    return;
                }
            }

            plan.Mode = BundleEditMode.InPlace;
            foreach (var edit in plan.Edits)
            {
                layout.TryMapUncompressed(edit.DataOffset, Hex.PPTR_SIZE, out var fileOffset);
                edit.FileOffset = fileOffset;
            }
        }

        private void PlanField(ScannedSerializedFile file, ScannedShaderField field)
        {
            var kind = field.ClassId switch
            {
                UnityClassId.MATERIAL => ShaderReferenceKind.Material,
                UnityClassId.MONO_BEHAVIOUR => ShaderReferenceKind.MonoBehaviour,
                _ => ShaderReferenceKind.Other,
            };

            // A PPtr with path ID 0 is a field nobody assigned.
            if (field.PathId == 0)
            {
                return;
            }

            var outcome = Resolve(file, field.FileId, field.PathId, out var shaderName, out var shaderCab);
            if (outcome == Resolution.OnReplacement)
            {
                if (kind == ShaderReferenceKind.Material)
                {
                    MaterialsOnReplacement++;
                }

                Report(file, field, kind, shaderName, "already on a replacement bundle");
                return;
            }

            if (outcome != Resolution.Found)
            {
                Skip(file, field, kind, shaderName, ReasonFor(outcome, shaderCab));
                return;
            }

            if (!_manifest.TryFind(shaderName, out var targets))
            {
                Skip(file, field, kind, shaderName, NOT_IN_MANIFEST);
                return;
            }

            var target = ChooseTarget(file, targets);
            var targetFileId = FileIdFor(file, target.Cab);
            _retargets[(shaderCab, field.PathId)] = (target, shaderName);
            _edits.Add(new PlannedEdit(
                file.Name,
                field.ObjectPathId,
                field.ObjectName,
                kind,
                field.FieldPath,
                shaderName,
                field.DataOffset,
                Hex.PPtr(field.FileId, field.PathId),
                Hex.PPtr(targetFileId, target.PathId),
                target));

            Report(file, field, kind, shaderName, "retargeted");
        }

        private void PlanPreload(ScannedSerializedFile file, ScannedPreloadEntry preload)
        {
            var cab = file.ResolveFileId(preload.FileId);
            if (cab is null || !_retargets.TryGetValue((cab, preload.PathId), out var retarget))
            {
                return;
            }

            var targetFileId = FileIdFor(file, retarget.Target.Cab);
            _edits.Add(new PlannedEdit(
                file.Name,
                preload.AssetBundlePathId,
                null,
                ShaderReferenceKind.Preload,
                preload.FieldPath,
                retarget.Name,
                preload.DataOffset,
                Hex.PPtr(preload.FileId, preload.PathId),
                Hex.PPtr(targetFileId, retarget.Target.PathId),
                retarget.Target));
        }

        private Resolution Resolve(ScannedSerializedFile file, int fileId, long pathId, out string shaderName, out string shaderCab)
        {
            shaderCab = file.ResolveFileId(fileId) ?? "";
            shaderName = $"<{(shaderCab.Length > 0 ? shaderCab : "file " + fileId)}:{pathId}>";

            if (shaderCab.Length == 0)
            {
                return Resolution.BadFileId;
            }

            if (_files.TryGetValue(shaderCab, out var owner) && owner.Shaders.TryGetValue(pathId, out var name))
            {
                shaderName = name;
            }

            if (_manifest.IsReplacementCab(shaderCab))
            {
                return Resolution.OnReplacement;
            }

            if (!shaderCab.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase))
            {
                return Resolution.BuiltinResource;
            }

            if (owner is null)
            {
                return Resolution.MissingBundle;
            }

            return owner.Shaders.ContainsKey(pathId) ? Resolution.Found : Resolution.NotAShader;
        }

        private static string ReasonFor(Resolution resolution, string cab) => resolution switch
        {
            Resolution.BadFileId => "file ID out of range",
            Resolution.BuiltinResource => $"built-in resource ({cab})",
            Resolution.MissingBundle => $"shader bundle not found ({cab})",
            Resolution.NotAShader => "referenced object is not a shader",
            _ => resolution.ToString(),
        };

        // A shader the manifest places in more than one replacement bundle goes to the one the file
        // already lists as an external, which keeps the edit same-size.
        private static ShaderTarget ChooseTarget(ScannedSerializedFile file, IReadOnlyList<ShaderTarget> targets)
        {
            foreach (var target in targets)
            {
                if (file.Externals.Contains(target.Cab, StringComparer.OrdinalIgnoreCase))
                {
                    return target;
                }
            }

            return targets[0];
        }

        private int FileIdFor(ScannedSerializedFile file, string cab)
        {
            for (var index = 0; index < file.Externals.Count; index++)
            {
                if (string.Equals(file.Externals[index], cab, StringComparison.OrdinalIgnoreCase))
                {
                    return index + 1;
                }
            }

            if (!_addedExternals.TryGetValue(file.Name, out var added))
            {
                added = [];
                _addedExternals.Add(file.Name, added);
            }

            var position = added.FindIndex(existing => string.Equals(existing, cab, StringComparison.OrdinalIgnoreCase));
            if (position < 0)
            {
                added.Add(cab);
                position = added.Count - 1;
            }

            return file.Externals.Count + position + 1;
        }

        private void Skip(ScannedSerializedFile file, ScannedShaderField field, ShaderReferenceKind kind, string shaderName, string reason)
        {
            Skipped.Add(new SkippedShaderReference(_bundle.File, kind, field.ObjectName, field.FieldPath, shaderName, reason));
            Report(file, field, kind, shaderName, "skipped: " + reason);
        }

        private void Report(ScannedSerializedFile file, ScannedShaderField field, ShaderReferenceKind kind, string shaderName, string outcome)
        {
            if (kind == ShaderReferenceKind.Material)
            {
                return;
            }

            FieldReferences.Add(new ShaderFieldReference(_bundle.File, kind, ScriptName(file, field), field.FieldPath, shaderName, outcome));
        }

        private string ScriptName(ScannedSerializedFile file, ScannedShaderField field)
        {
            if (field.ClassId != UnityClassId.MONO_BEHAVIOUR)
            {
                return field.ObjectName ?? $"class {field.ClassId}";
            }

            var cab = file.ResolveFileId(field.ScriptFileId);
            if (cab is not null && _files.TryGetValue(cab, out var owner) && owner.MonoScripts.TryGetValue(field.ScriptPathId, out var name))
            {
                return name;
            }

            return $"<script {cab ?? "file " + field.ScriptFileId}:{field.ScriptPathId}>";
        }
    }

    private enum Resolution
    {
        Found,
        OnReplacement,
        BadFileId,
        BuiltinResource,
        MissingBundle,
        NotAShader,
    }
}
