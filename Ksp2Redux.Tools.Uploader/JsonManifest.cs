using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Uploader;

public class JsonManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }
    
    [JsonPropertyName("channel")]
    public string Channel { get; set; }

    [JsonPropertyName("generatedAt")] 
    public DateTime GeneratedAt { get; set; }
    
    [JsonPropertyName("patches")]
    public JsonPatch[] Patches { get; set; }
}