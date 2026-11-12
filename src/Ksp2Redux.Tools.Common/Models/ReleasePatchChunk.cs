using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.Models;

/// <summary>
/// A verified transport part of a release patch.
/// </summary>
public sealed record ReleasePatchChunk
{
    /// <summary>
    /// Gets the zero-based position of the part in the patch.
    /// </summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    /// <summary>
    /// Gets the exact part size in bytes.
    /// </summary>
    [JsonPropertyName("size")]
    public required long Size { get; init; }

    /// <summary>
    /// Gets the SHA-256 checksum of the part.
    /// </summary>
    [JsonPropertyName("checksum_sha256")]
    public required string ChecksumSha256 { get; init; }

    /// <summary>
    /// Gets the Cloudflare R2 download URL.
    /// </summary>
    [JsonPropertyName("r2")]
    public required string R2Url { get; init; }

    /// <summary>
    /// Gets the GitHub backup download URL.
    /// </summary>
    [JsonPropertyName("github")]
    public required string GitHubUrl { get; init; }
}
