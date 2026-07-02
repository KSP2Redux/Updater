using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common;

public sealed record ReleasePatch
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Label { get; init; }

    [JsonPropertyName("requires")]
    public required PatchRequirement Requires { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("checksum_sha256")]
    public required string ChecksumSha256 { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }

    [JsonPropertyName("releasedAt")]
    public required DateTime ReleasedAt { get; init; }
}
