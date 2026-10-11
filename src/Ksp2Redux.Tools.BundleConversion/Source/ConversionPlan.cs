namespace Ksp2Redux.Tools.BundleConversion;

/// <summary>
/// What kind of serialized reference an edit or a skipped reference is.
/// </summary>
public enum ShaderReferenceKind
{
    /// <summary>A Material's m_Shader.</summary>
    Material,

    /// <summary>A PPtr&lt;Shader&gt; field of a MonoBehaviour.</summary>
    MonoBehaviour,

    /// <summary>A PPtr&lt;Shader&gt; field of any other object type.</summary>
    Other,

    /// <summary>An AssetBundle preload table entry that pointed at a retargeted shader.</summary>
    Preload,
}

/// <summary>
/// How a bundle's edits are made.
/// </summary>
public enum BundleEditMode
{
    /// <summary>Same-size byte edits in the stock file.</summary>
    InPlace,

    /// <summary>A converted copy under the Redux folder, leaving the stock file alone.</summary>
    Rewrite,
}

/// <summary>
/// One retargeted PPtr.
/// </summary>
public sealed class PlannedEdit
{
    internal PlannedEdit(
        string serializedFile,
        long objectPathId,
        string? objectName,
        ShaderReferenceKind kind,
        string fieldPath,
        string shaderName,
        long dataOffset,
        byte[] oldBytes,
        byte[] newBytes,
        ShaderTarget target)
    {
        SerializedFile = serializedFile;
        ObjectPathId = objectPathId;
        ObjectName = objectName;
        Kind = kind;
        FieldPath = fieldPath;
        ShaderName = shaderName;
        DataOffset = dataOffset;
        Old = oldBytes;
        New = newBytes;
        Target = target;
    }

    /// <summary>Gets the serialized file (CAB) inside the bundle that holds the PPtr.</summary>
    public string SerializedFile { get; }

    /// <summary>Gets the path ID of the object holding the PPtr.</summary>
    public long ObjectPathId { get; }

    /// <summary>Gets the object's name, when it has one.</summary>
    public string? ObjectName { get; }

    /// <summary>Gets what kind of reference the PPtr is.</summary>
    public ShaderReferenceKind Kind { get; }

    /// <summary>Gets the field path inside the object.</summary>
    public string FieldPath { get; }

    /// <summary>Gets the name of the shader the PPtr points at, before and after.</summary>
    public string ShaderName { get; }

    /// <summary>Gets the offset of the PPtr in the bundle's decompressed data stream.</summary>
    public long DataOffset { get; }

    /// <summary>Gets the offset of the PPtr in the bundle file, or -1 when it sits in a compressed block.</summary>
    public long FileOffset { get; internal set; } = -1;

    /// <summary>Gets the stock 12 bytes.</summary>
    public byte[] Old { get; }

    /// <summary>Gets the converted 12 bytes.</summary>
    public byte[] New { get; }

    /// <summary>Gets where the PPtr is retargeted to.</summary>
    public ShaderTarget Target { get; }
}

/// <summary>
/// The edits planned for one bundle.
/// </summary>
public sealed class BundleEditPlan
{
    internal BundleEditPlan(string file, long size, IReadOnlyList<PlannedEdit> edits, IReadOnlyDictionary<string, IReadOnlyList<string>> addedExternals)
    {
        File = file;
        Size = size;
        Edits = edits;
        AddedExternals = addedExternals;
    }

    /// <summary>Gets the bundle file name.</summary>
    public string File { get; }

    /// <summary>Gets the stock bundle size.</summary>
    public long Size { get; }

    /// <summary>Gets how the edits are made.</summary>
    public BundleEditMode Mode { get; internal set; }

    /// <summary>Gets why the bundle needs a converted copy, or null when it is edited in place.</summary>
    public string? RewriteReason { get; internal set; }

    /// <summary>Gets the edits.</summary>
    public IReadOnlyList<PlannedEdit> Edits { get; }

    /// <summary>
    /// Gets the CABs each serialized file has to gain as externals, appended after its existing ones.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AddedExternals { get; }

    /// <summary>Gets a value indicating whether every edit keeps the file layout, so only compression stands in the way of an in-place edit.</summary>
    public bool IsSameSize => AddedExternals.Count == 0;
}

/// <summary>
/// A shader reference the plan leaves alone.
/// </summary>
/// <param name="File">The bundle holding the reference.</param>
/// <param name="Kind">What kind of reference it is.</param>
/// <param name="ObjectName">The name of the object holding it, when it has one.</param>
/// <param name="FieldPath">The field path inside the object.</param>
/// <param name="Shader">The shader name, or CAB:pathId when the name could not be read.</param>
/// <param name="Reason">Why it was left alone.</param>
public sealed record SkippedShaderReference(
    string File,
    ShaderReferenceKind Kind,
    string? ObjectName,
    string FieldPath,
    string Shader,
    string Reason);

/// <summary>
/// A PPtr&lt;Shader&gt; serialized on a MonoBehaviour or another non-material object, and what the plan does with it.
/// </summary>
/// <param name="File">The bundle holding the reference.</param>
/// <param name="Kind">MonoBehaviour or Other.</param>
/// <param name="Script">The MonoBehaviour's script class, or the object's name for other types.</param>
/// <param name="FieldPath">The field path inside the object.</param>
/// <param name="Shader">The shader name, or CAB:pathId when the name could not be read.</param>
/// <param name="Outcome">What the plan does: retargeted, already on a replacement bundle, or why it was skipped.</param>
public sealed record ShaderFieldReference(
    string File,
    ShaderReferenceKind Kind,
    string Script,
    string FieldPath,
    string Shader,
    string Outcome);

/// <summary>
/// A bundle that could not be read, or a serialized file inside one with objects that could not be.
/// </summary>
/// <param name="File">The bundle file name.</param>
/// <param name="Message">What went wrong.</param>
public sealed record BundleProblem(string File, string Message);

/// <summary>
/// The result of scanning a bundle folder against a shader manifest.
/// </summary>
public sealed class ConversionPlan
{
    internal ConversionPlan(
        string manifestSha256,
        int bundlesScanned,
        IReadOnlyList<BundleEditPlan> bundles,
        IReadOnlyList<SkippedShaderReference> skipped,
        IReadOnlyList<ShaderFieldReference> fieldReferences,
        IReadOnlyList<BundleProblem> errors,
        IReadOnlyList<BundleProblem> warnings,
        int materialsOnReplacement)
    {
        ManifestSha256 = manifestSha256;
        BundlesScanned = bundlesScanned;
        Bundles = bundles;
        Skipped = skipped;
        FieldReferences = fieldReferences;
        Errors = errors;
        Warnings = warnings;
        MaterialsOnReplacement = materialsOnReplacement;
    }

    /// <summary>Gets the SHA-256 of the manifest the plan was made against.</summary>
    public string ManifestSha256 { get; }

    /// <summary>Gets the number of bundle files scanned.</summary>
    public int BundlesScanned { get; }

    /// <summary>Gets the bundles with at least one edit, ordered by file name.</summary>
    public IReadOnlyList<BundleEditPlan> Bundles { get; }

    /// <summary>Gets every shader reference left alone.</summary>
    public IReadOnlyList<SkippedShaderReference> Skipped { get; }

    /// <summary>Gets every PPtr&lt;Shader&gt; outside materials, with what the plan does with it.</summary>
    public IReadOnlyList<ShaderFieldReference> FieldReferences { get; }

    /// <summary>Gets bundles that could not be read at all.</summary>
    public IReadOnlyList<BundleProblem> Errors { get; }

    /// <summary>Gets objects that could not be read inside otherwise readable bundles.</summary>
    public IReadOnlyList<BundleProblem> Warnings { get; }

    /// <summary>Gets the number of materials that already point into a replacement shader bundle.</summary>
    public int MaterialsOnReplacement { get; }

    /// <summary>Gets the edits of one kind across all bundles of one mode.</summary>
    /// <param name="mode">The edit mode.</param>
    /// <param name="kind">The reference kind.</param>
    /// <returns>The number of edits.</returns>
    public int CountEdits(BundleEditMode mode, ShaderReferenceKind kind) =>
        Bundles.Where(bundle => bundle.Mode == mode).Sum(bundle => bundle.Edits.Count(edit => edit.Kind == kind));
}
