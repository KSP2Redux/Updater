using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.Patching;

public sealed record PatchOperation
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("action")]
    public required PatchAction Action { get; init; }

    [JsonPropertyName("originalHash")]
    public byte[]? OriginalHash { get; init; }

    [JsonPropertyName("finalHash")]
    public byte[]? FinalHash { get; init; }

    public enum PatchAction
    {
        Patch,
        Add,
        Remove,
    }
}
