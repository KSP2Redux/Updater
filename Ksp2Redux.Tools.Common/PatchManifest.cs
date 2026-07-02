using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common;

public sealed record PatchManifest
{
    [JsonPropertyName("operations")]
    public List<PatchOperation> Operations { get; init; } = [];
}
