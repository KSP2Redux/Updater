using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.Patching;

public sealed record PatchManifest
{
    [JsonPropertyName("operations")]
    public List<PatchOperation> Operations { get; init; } = [];
}
