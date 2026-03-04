using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Uploader;

public class JsonRequires
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}