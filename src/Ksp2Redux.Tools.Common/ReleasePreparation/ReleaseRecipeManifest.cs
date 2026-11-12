using System.Text.Json.Serialization;
using Ksp2Redux.Tools.BundleConversion.Bundles;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Common.ReleasePreparation;

/// <summary>
/// A versioned local release artifact containing complete target and supported baseline contracts.
/// </summary>
/// <param name="Version">The release container schema version.</param>
/// <param name="PackProfile">The pinned literal compression and container profile.</param>
/// <param name="TargetManifest">The canonical complete target tree contract bytes.</param>
/// <param name="Variants">The alternative baseline recipe sets.</param>
/// <param name="Packs">The independently hashed literal packs in numeric filename order.</param>
/// <param name="Chunks">The unique bounded literal records.</param>
public record ReleaseRecipeManifest(int Version, string PackProfile, byte[] TargetManifest,
    List<ReleaseRecipeVariant> Variants, List<ReleaseRecipePack> Packs, List<ReleaseRecipeChunk> Chunks);

/// <summary>
/// A complete recipe set for one expected stock source contract.
/// </summary>
/// <param name="SourceManifest">The canonical independently supplied baseline contract bytes.</param>
/// <param name="Sources">The ordered baseline paths and source coordinate spaces.</param>
/// <param name="Files">Exact recipes for the target files that need reconstruction.</param>
public record ReleaseRecipeVariant(byte[] SourceManifest, List<ReleaseRecipeSource> Sources,
    List<ReleaseFileRecipe> Files)
{
    /// <summary>
    /// Gets a map from exact target paths to byte-identical paths in the same variant's baseline.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ExactCopies { get; init; }
}

/// <summary>
/// A baseline file exposed to the existing recipe matcher.
/// </summary>
/// <param name="Path">The normalized path in the baseline contract.</param>
/// <param name="Kind">The raw or decompressed UnityFS coordinate space.</param>
public record ReleaseRecipeSource(string Path, BundleRecipeSourceKind Kind);

/// <summary>
/// An exact file recipe with its virtual literal-stream chunk order.
/// </summary>
/// <param name="Path">The normalized path in the target tree.</param>
/// <param name="Recipe">The existing versioned exact-file recipe.</param>
/// <param name="PayloadChunks">Chunk indices concatenated into the recipe's original literal stream.</param>
public record ReleaseFileRecipe(string Path, BundleRecipe Recipe, List<int> PayloadChunks);

/// <summary>
/// The independent identity of a pack stored as pack-NNNNNN.bin.
/// </summary>
/// <param name="Length">The exact stored byte count within the selected pack profile.</param>
/// <param name="Sha256">The exact stored SHA-256 digest.</param>
public record ReleaseRecipePack(int Length, string Sha256);

/// <summary>
/// An independently verified chunk range in a hashed pack.
/// </summary>
/// <param name="Sha256">The uncompressed SHA-256 identity.</param>
/// <param name="Length">The positive uncompressed byte count, at most 128 KiB.</param>
/// <param name="PackIndex">The containing pack index.</param>
/// <param name="Offset">The stored range start.</param>
/// <param name="StoredLength">The stored range length.</param>
/// <param name="IsCompressed">True for pinned LZ4 HC blocks, false for raw storage.</param>
public record ReleaseRecipeChunk(string Sha256, int Length, int PackIndex, int Offset,
    int StoredLength, bool IsCompressed);

/// <summary>
/// Publisher inputs for one separately frozen and independently described source tree.
/// </summary>
/// <param name="Root">The isolated baseline root.</param>
/// <param name="Manifest">The expected baseline files, never inferred by the publisher.</param>
/// <param name="Sources">The selected matching sources, in deterministic descriptor order.</param>
/// <param name="RangeHints">The optional target-path mappings to verified source range claims.</param>
public record ReleaseRecipeSourceTree(string Root, StockBaselineManifest Manifest,
    IReadOnlyList<ReleaseRecipeSource> Sources,
    IReadOnlyDictionary<string, IReadOnlyList<BundleRecipeRangeHint>>? RangeHints = null);

/// <summary>
/// Local publication measurements and digests without any authentication claim.
/// </summary>
/// <param name="ArtifactSha256">The release recipe manifest digest.</param>
/// <param name="TargetManifestSha256">The complete target contract digest.</param>
/// <param name="SourceReusedBytes">Source ranges across every generated variant.</param>
/// <param name="LogicalLiteralBytes">Literal bytes referenced across every target and variant.</param>
/// <param name="UniqueLiteralBytes">Unique uncompressed chunk bytes stored across the release.</param>
/// <param name="StoredPackBytes">Stored pack bytes after raw-versus-LZ4 selection.</param>
/// <param name="CrossRecipeDeduplicatedBytes">Per-recipe unique literals eliminated by release deduplication.</param>
public record ReleaseRecipePublication(string ArtifactSha256, string TargetManifestSha256,
    long SourceReusedBytes, long LogicalLiteralBytes, long UniqueLiteralBytes, long StoredPackBytes,
    long CrossRecipeDeduplicatedBytes);
