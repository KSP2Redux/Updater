using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Uploader;

public class JsonPatch
{
    [JsonPropertyName("version")]
    public string Version { get; set; }
    
    [JsonPropertyName("requires")]
    public JsonRequires Requires { get; set; }
    
    [JsonPropertyName("url")]
    public string Url { get; set; }
    
    [JsonPropertyName("checksum_sha256")]
    public string ChecksumSha256 { get; set; }
    
    [JsonPropertyName("size")]
    public long Size { get; set; }
    
    [JsonPropertyName("releasedAt")]
    public DateTime ReleasedAt { get; set; }
}