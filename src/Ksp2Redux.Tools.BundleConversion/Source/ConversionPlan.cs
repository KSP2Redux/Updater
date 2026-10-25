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
/// One cubemap the plan stores in a smaller format, or only stops keeping a CPU copy of.
/// </summary>
/// <remarks>
/// Neither changes the stock file. The new header bytes and pixels are journaled as an overlay that whoever loads the
/// bundle shows over the stock bytes, the way a cache conversion's in-place edits are, and the pixels go to a file of
/// their own beside the copies. Same-size fields and an untouched bundle layout are why only uncompressed stock blocks
/// qualify: the new pixels are written over the start of the old ones, which are never read again.
/// </remarks>
public sealed class PlannedTexture
{
    internal PlannedTexture(
        string serializedFile,
        long objectPathId,
        string name,
        int sourceFormat,
        int targetFormat,
        int width,
        int height,
        int mipCount,
        long sourceFileOffset,
        long sourceFaceSize,
        long targetFaceSize,
        IReadOnlyList<TextureFieldEdit> fieldEdits)
    {
        SerializedFile = serializedFile;
        ObjectPathId = objectPathId;
        Name = name;
        SourceFormat = sourceFormat;
        TargetFormat = targetFormat;
        Width = width;
        Height = height;
        MipCount = mipCount;
        SourceFileOffset = sourceFileOffset;
        SourceFaceSize = sourceFaceSize;
        TargetFaceSize = targetFaceSize;
        FieldEdits = fieldEdits;
    }

    /// <summary>Gets the serialized file (CAB) inside the bundle that holds the texture.</summary>
    public string SerializedFile { get; }

    /// <summary>Gets the texture's path ID.</summary>
    public long ObjectPathId { get; }

    /// <summary>Gets the texture's name.</summary>
    public string Name { get; }

    /// <summary>Gets the stock Unity texture format.</summary>
    public int SourceFormat { get; }

    /// <summary>Gets the converted Unity texture format, the stock one when only the CPU copy goes.</summary>
    public int TargetFormat { get; }

    /// <summary>Gets the top mip's width.</summary>
    public int Width { get; }

    /// <summary>Gets the top mip's height.</summary>
    public int Height { get; }

    /// <summary>Gets the number of mips.</summary>
    public int MipCount { get; }

    /// <summary>Gets the file offset of the stock pixels, or -1 when the pixels stay as they are.</summary>
    public long SourceFileOffset { get; }

    /// <summary>Gets the stock size of one face with its mips.</summary>
    public long SourceFaceSize { get; }

    /// <summary>Gets the converted size of one face with its mips.</summary>
    public long TargetFaceSize { get; }

    /// <summary>Gets a value indicating whether the pixels change format, rather than the texture only stopping being readable.</summary>
    public bool ChangesFormat => TargetFormat != SourceFormat;

    /// <summary>Gets the bytes the texture takes on the GPU before the conversion.</summary>
    public long StockBytes => SourceFaceSize * Textures.CubemapRecompression.FACES;

    /// <summary>Gets the bytes it takes after.</summary>
    public long ConvertedBytes => TargetFaceSize * Textures.CubemapRecompression.FACES;

    /// <summary>Gets the same-size header edits.</summary>
    internal IReadOnlyList<TextureFieldEdit> FieldEdits { get; }
}

/// <summary>
/// One same-size edit to a texture header field.
/// </summary>
/// <param name="Field">The field path.</param>
/// <param name="FileOffset">The field's offset in the bundle file.</param>
/// <param name="Old">The stock bytes.</param>
/// <param name="New">The converted bytes.</param>
internal sealed record TextureFieldEdit(string Field, long FileOffset, byte[] Old, byte[] New);

/// <summary>
/// The textures planned for one bundle.
/// </summary>
/// <param name="File">The bundle file name.</param>
/// <param name="Size">The stock bundle size.</param>
/// <param name="Textures">The textures, in file order.</param>
public sealed record BundleTexturePlan(string File, long Size, IReadOnlyList<PlannedTexture> Textures);

/// <summary>
/// A cloud cubemap the plan leaves in its stock format.
/// </summary>
/// <param name="File">The bundle holding it.</param>
/// <param name="Name">The texture's name.</param>
/// <param name="Reason">Why it stays as it is.</param>
public sealed record SkippedTexture(string File, string Name, string Reason);

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
        int materialsOnReplacement,
        IReadOnlyList<string> unrecordedEdits,
        IReadOnlyList<BundleTexturePlan> textureBundles,
        IReadOnlyList<SkippedTexture> skippedTextures)
    {
        ManifestSha256 = manifestSha256;
        BundlesScanned = bundlesScanned;
        Bundles = bundles;
        Skipped = skipped;
        FieldReferences = fieldReferences;
        Errors = errors;
        Warnings = warnings;
        MaterialsOnReplacement = materialsOnReplacement;
        UnrecordedEdits = unrecordedEdits;
        TextureBundles = textureBundles;
        SkippedTextures = skippedTextures;
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

    /// <summary>
    /// Gets the bundles holding converted shader references the journal does not record. Only a stock copy of the
    /// bundle can put them back, and until then a build without the replacement shaders draws them magenta.
    /// </summary>
    public IReadOnlyList<string> UnrecordedEdits { get; }

    /// <summary>Gets the bundles whose cloud cubemaps get a smaller format or lose their CPU copy, ordered by file name.</summary>
    public IReadOnlyList<BundleTexturePlan> TextureBundles { get; }

    /// <summary>Gets the cloud cubemaps left in their stock format for a reason other than already being small.</summary>
    public IReadOnlyList<SkippedTexture> SkippedTextures { get; }

    /// <summary>Gets the edits of one kind across all bundles of one mode.</summary>
    /// <param name="mode">The edit mode.</param>
    /// <param name="kind">The reference kind.</param>
    /// <returns>The number of edits.</returns>
    public int CountEdits(BundleEditMode mode, ShaderReferenceKind kind) =>
        Bundles.Where(bundle => bundle.Mode == mode).Sum(bundle => bundle.Edits.Count(edit => edit.Kind == kind));
}
