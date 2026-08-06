using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.Models;

public sealed record PatchRequirement
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonIgnore]
    public bool IsBasePatch => string.IsNullOrEmpty(Version);
}
