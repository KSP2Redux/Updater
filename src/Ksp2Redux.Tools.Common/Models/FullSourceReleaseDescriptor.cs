using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Common.Models;

/// <summary>
/// Publisher metadata binding an exact release artifact to complete target and stock contracts.
/// </summary>
/// <param name="ReleaseId">The immutable release selector.</param>
/// <param name="Channel">The containing feed channel.</param>
/// <param name="Version">The containing patch version.</param>
/// <param name="TargetManifest">The canonical complete target contract bytes.</param>
/// <param name="TargetManifestSha256">The independently published target contract digest.</param>
/// <param name="R2ArtifactUrl">The HTTPS release.json URL on the primary source.</param>
/// <param name="GitHubArtifactUrl">The HTTPS release.json URL on the backup source.</param>
/// <param name="Baselines">The supported complete publisher-curated storefront definitions.</param>
/// <param name="DownloadBytes">The artifact and pack bytes used for planning.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FullSourceReleaseDescriptor(string ReleaseId, string Channel, string Version,
    byte[] TargetManifest, string TargetManifestSha256, string R2ArtifactUrl, string GitHubArtifactUrl,
    List<FullSourceBaselineDefinition> Baselines, long DownloadBytes)
{
    /// <summary>
    /// Gets the boot.config keys and values the release's player was built with, without its build identity.
    /// </summary>
    /// <remarks>
    /// The launcher writes them into the installed boot.config outside the install transaction.
    /// Unlike the other optional fields, a launcher that predates this one rejects a descriptor carrying it.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? BootConfigSettings { get; init; }

    /// <summary>
    /// Gets the retired boot derivation permission, which older descriptors carry. Its value has no effect.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowsDerivedBootConfig { get; init; }

    /// <summary>
    /// Gets the common prepatched tree used by initial recipes, independently of original stock backup contracts.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? PrepatchManifest { get; init; }

    /// <summary>
    /// Gets the independently pinned digest of the common prepatch contract.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrepatchManifestSha256 { get; init; }

    /// <summary>
    /// Gets the exact embedded normalization package digest for each supported storefront.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? PrepatchResources { get; init; }

    /// <summary>
    /// Gets the complete target contract digests of prior Redux releases supported as update sources.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? UpdateSourceDigests { get; init; }

    /// <summary>
    /// Gets independently downloadable recipes for supported installed Redux releases.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FullSourceUpdateArtifact>? UpdateArtifacts { get; init; }

    /// <summary>
    /// Get the target contract in the form the launcher keeps: the contract's own serialization, without boot.config.
    /// </summary>
    /// <remarks>
    /// A release published before boot.config left every contract lists it in <see cref="TargetManifest"/>. Both forms
    /// read as the same contract and give the same bytes here.
    /// </remarks>
    /// <returns>The canonical contract bytes.</returns>
    public byte[] GetNormalizedTargetManifest() => StockBaselineManifest.FromJson(TargetManifest).ToJson();

    /// <summary>
    /// Get the digest that names this release's contract everywhere outside the artifact marker check.
    /// </summary>
    /// <remarks>
    /// Installed catalogs, plans and prepared targets carry this digest, so an install and the feed agree on a release
    /// whichever form of its contract was published. <see cref="TargetManifestSha256"/> stays the digest of the published
    /// bytes, which only the artifact marker and the descriptor's own validation compare.
    /// </remarks>
    /// <returns>The upper-case SHA-256 of the canonical contract bytes.</returns>
    public string GetNormalizedTargetManifestSha256() => Convert.ToHexString(SHA256.HashData(GetNormalizedTargetManifest()));
}

/// <summary>
/// Publisher metadata for a separate update recipe and its packs.
/// </summary>
/// <param name="SourceDigest">The canonical installed release contract digest.</param>
/// <param name="MarkerSha256">The independently published update marker digest.</param>
/// <param name="MarkerSize">The expected marker byte length.</param>
/// <param name="DownloadBytes">The expected marker and pack bytes.</param>
/// <param name="R2ArtifactUrl">The primary update marker URL.</param>
/// <param name="GitHubArtifactUrl">The backup update marker URL.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FullSourceUpdateArtifact(string SourceDigest, string MarkerSha256, long MarkerSize,
    long DownloadBytes, string R2ArtifactUrl, string GitHubArtifactUrl)
{
    /// <summary>
    /// Gets the hosted prefix that keeps the update's files separate from the stock artifact.
    /// </summary>
    [JsonIgnore]
    public string AssetPrefix => $"update-{SourceDigest.ToLowerInvariant()}-";
}

/// <summary>
/// A complete expected baseline supplied by the release publisher, independently of observed local bytes.
/// </summary>
/// <remarks>
/// A publisher contract is not an independently verified Steam signature. Depot selectors enable
/// acquisition through the existing signed-in Steam transport. Other storefronts require matching local bytes.
/// </remarks>
/// <param name="Manifest">The canonical complete stock file and directory contract.</param>
/// <param name="SteamDepots">The exact Windows depot selectors, or an empty list for local-only acquisition.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FullSourceBaselineDefinition(byte[] Manifest, List<FullSourceSteamDepot> SteamDepots);

/// <summary>
/// A publisher-pinned Steam depot and manifest selector.
/// </summary>
/// <param name="DepotId">The depot identity.</param>
/// <param name="ManifestId">The immutable manifest identity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record FullSourceSteamDepot(uint DepotId, ulong ManifestId);
