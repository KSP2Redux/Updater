using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common;

public sealed record ReleaseManifest
{
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; set; }

    [JsonPropertyName("channel")]
    public required string Channel { get; set; }

    [JsonPropertyName("generatedAt")]
    public required DateTime GeneratedAt { get; set; }

    [JsonPropertyName("patches")]
    public required List<ReleasePatch> Patches { get; set; }
}
