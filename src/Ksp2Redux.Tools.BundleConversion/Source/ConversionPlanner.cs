using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.BundleConversion.Textures;

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

        List<SkippedTexture> skippedTextures = [];
        var textureBundles = PlanTextures(bundles, plans, skippedTextures);

        return new ConversionPlan(
            manifest.ManifestSha256,
            bundles.Count,
            plans,
            skipped,
            fieldReferences,
            errors,
            warnings,
            materialsOnReplacement,
            FindUnrecordedEdits(bundles),
            textureBundles,
            skippedTextures);
    }

    /// <summary>
    /// Plans the cloud cubemaps that get a smaller format, and the ones that only stop keeping a CPU copy.
    /// </summary>
    /// <param name="bundles">Every scanned bundle.</param>
    /// <param name="shaderPlans">The shader plans, which say what else happens to each bundle.</param>
    /// <param name="skipped">Receives the cloud cubemaps left as they are, with why.</param>
    /// <returns>The texture plans, ordered by bundle file name.</returns>
    // A bundle written as a converted copy is left out, because the game loads the copy instead of the stock file the
    // overlay would sit on.
    internal static List<BundleTexturePlan> PlanTextures(
        IReadOnlyList<ScannedBundle> bundles,
        IReadOnlyList<BundleEditPlan> shaderPlans,
        List<SkippedTexture> skipped)
    {
        var copied = new HashSet<string>(
            shaderPlans.Where(plan => plan.Mode == BundleEditMode.Rewrite).Select(plan => plan.File),
            StringComparer.OrdinalIgnoreCase);

        List<BundleTexturePlan> result = [];
        foreach (var bundle in bundles.Where(bundle => bundle.Error is null).OrderBy(bundle => bundle.File, StringComparer.Ordinal))
        {
            List<PlannedTexture> textures = [];
            foreach (var file in bundle.Files)
            {
                foreach (var texture in file.Textures.Where(texture => CubemapRecompression.IsCloudMap(texture.Name)))
                {
                    var reason = copied.Contains(bundle.File) ? "the bundle is written as a converted copy" : null;
                    var planned = reason is null ? PlanTexture(bundle, file, texture, out reason) : null;
                    if (planned is not null)
                    {
                        textures.Add(planned);
                    }
                    else if (reason is not null)
                    {
                        skipped.Add(new SkippedTexture(bundle.File, texture.Name, reason));
                    }
                }
            }

            if (textures.Count > 0)
            {
                result.Add(new BundleTexturePlan(bundle.File, bundle.Size, textures));
            }
        }

        return result;
    }

    // Returns null with a null reason for a texture that is already as small as the conversion makes it.
    private static PlannedTexture? PlanTexture(ScannedBundle bundle, ScannedSerializedFile file, ScannedTexture texture, out string? reason)
    {
        reason = null;
        var format = (int)texture.Value(ScannedTexture.FORMAT);
        var width = (int)texture.Value(ScannedTexture.WIDTH);
        var height = (int)texture.Value(ScannedTexture.HEIGHT);
        var mipCount = (int)texture.Value(ScannedTexture.MIP_COUNT);
        var faceSize = texture.Value(ScannedTexture.COMPLETE_IMAGE_SIZE);
        if (width <= 0 || height <= 0 || mipCount <= 0 || faceSize <= 0)
        {
            reason = "its header could not be read";
            return null;
        }

        // A readable texture keeps a CPU copy of its pixels for scripts, and nothing reads the cloud maps that way.
        // Its pixels sit inside the object, where a smaller format would change the object's size.
        if (texture.Value(ScannedTexture.IS_READABLE) == 1)
        {
            var readable = FieldEdit(bundle, texture, ScannedTexture.IS_READABLE, 0, ref reason);
            return readable is null
                ? null
                : new PlannedTexture(file.Name, texture.ObjectPathId, texture.Name, format, format, width, height, mipCount, -1, faceSize, faceSize, [readable]);
        }

        if (!CubemapRecompression.TryChooseFormat(texture.Name, format, out var target))
        {
            return null;
        }

        if (texture.Value(ScannedTexture.IMAGE_COUNT) != CubemapRecompression.FACES ||
            CubemapRecompression.FaceSize(format, width, height, mipCount) != faceSize ||
            texture.Value(ScannedTexture.STREAM_SIZE) != faceSize * CubemapRecompression.FACES)
        {
            reason = "its pixel data is not laid out as six faces with full mip chains";
            return null;
        }

        var resource = BundleScanner.ExternalName(texture.StreamPath);
        var entry = bundle.Entries.FirstOrDefault(candidate => string.Equals(candidate.Name, resource, StringComparison.Ordinal));
        if (entry is null)
        {
            reason = $"its pixels are not in a resource file of this bundle ({texture.StreamPath})";
            return null;
        }

        if (texture.Value(ScannedTexture.STREAM_OFFSET) < 0 || texture.Value(ScannedTexture.STREAM_OFFSET) + (faceSize * CubemapRecompression.FACES) > entry.Size)
        {
            reason = "its pixels run past the end of their resource file";
            return null;
        }

        var targetFaceSize = CubemapRecompression.FaceSize(target, width, height, mipCount);
        var dataOffset = entry.DataOffset + texture.Value(ScannedTexture.STREAM_OFFSET);
        if (!bundle.Layout!.TryMapUncompressed(dataOffset, (int)(targetFaceSize * CubemapRecompression.FACES), out var fileOffset))
        {
            reason = "its pixels are stored in compressed blocks";
            return null;
        }

        TextureFieldEdit?[] edits =
        [
            FieldEdit(bundle, texture, ScannedTexture.FORMAT, target, ref reason),
            FieldEdit(bundle, texture, ScannedTexture.COMPLETE_IMAGE_SIZE, targetFaceSize, ref reason),
            FieldEdit(bundle, texture, ScannedTexture.STREAM_SIZE, targetFaceSize * CubemapRecompression.FACES, ref reason),
        ];

        return reason is not null
            ? null
            : new PlannedTexture(
                file.Name,
                texture.ObjectPathId,
                texture.Name,
                format,
                target,
                width,
                height,
                mipCount,
                fileOffset,
                faceSize,
                targetFaceSize,
                [.. edits.Select(edit => edit!)]);
    }

    private static TextureFieldEdit? FieldEdit(ScannedBundle bundle, ScannedTexture texture, string field, long value, ref string? reason)
    {
        if (!texture.Fields.TryGetValue(field, out var number))
        {
            reason ??= $"its {field} could not be read";
            return null;
        }

        if (!bundle.Layout!.TryMapUncompressed(texture.ObjectDataOffset + number.Offset, number.Size, out var fileOffset))
        {
            reason ??= "its header is stored in compressed blocks";
            return null;
        }

        return new TextureFieldEdit(field, fileOffset, LittleEndian(number.Value, number.Size), LittleEndian(value, number.Size));
    }

    private static byte[] LittleEndian(long value, int size)
    {
        var bytes = new byte[size];
        for (var index = 0; index < size; index++)
        {
            bytes[index] = (byte)(value >> (index * 8));
        }

        return bytes;
    }

    /// <summary>
    /// Finds bundles holding in-place edits nobody can put back: references into the stock shader bundles at path
    /// IDs those bundles do not contain. A stock bundle only ever points at objects that exist, so such a
    /// reference is a retarget to a shader only a replacement bundle holds. Edits a journal records are not
    /// found when the scan overlaid their stock bytes.
    /// </summary>
    /// <param name="bundles">Every scanned bundle, including the stock shader bundles.</param>
    /// <returns>The bundle file names, ordered.</returns>
    public static IReadOnlyList<string> FindUnrecordedEdits(IReadOnlyList<ScannedBundle> bundles)
    {
        Dictionary<string, ScannedSerializedFile> shaderFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in bundles.Where(bundle => bundle.Error is null).SelectMany(bundle => bundle.Files))
        {
            if ((string.Equals(file.Name, ShaderBundleIds.SHARED_SHADERS_CAB, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(file.Name, ShaderBundleIds.BUILTIN_SHADERS_CAB, StringComparison.OrdinalIgnoreCase)) &&
                !shaderFiles.ContainsKey(file.Name))
            {
                shaderFiles.Add(file.Name, file);
            }
        }

        // Without a stock shader bundle there is nothing to tell a retargeted reference from a stock one by.
        if (shaderFiles.Count == 0)
        {
            return [];
        }

        return bundles
            .Where(bundle => bundle.Error is null && bundle.Files.Any(file =>
                file.ShaderFields.Any(field => IsDangling(file, field.FileId, field.PathId)) ||
                file.Preloads.Any(preload => IsDangling(file, preload.FileId, preload.PathId))))
            .Select(bundle => bundle.File)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        bool IsDangling(ScannedSerializedFile file, int fileId, long pathId)
        {
            if (pathId == 0)
            {
                return false;
            }

            var cab = file.ResolveFileId(fileId);
            return cab is not null && shaderFiles.TryGetValue(cab, out var owner) && !owner.ObjectIds.Contains(pathId);
        }
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
