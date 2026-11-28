using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.Models;

public sealed record ReleasePatch
{
    /// <summary>
    /// Gets exact full-source preparation metadata, or null for a legacy patch.
    /// </summary>
    [JsonPropertyName("fullSource"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FullSourceReleaseDescriptor? FullSource { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Label { get; init; }

    [JsonPropertyName("requires")]
    public required PatchRequirement Requires { get; init; }

    [JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    [JsonPropertyName("chunks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReleasePatchChunk>? Chunks { get; init; }

    [JsonPropertyName("checksum_sha256")]
    public required string ChecksumSha256 { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }

    [JsonPropertyName("releasedAt")]
    public required DateTime ReleasedAt { get; init; }
}
