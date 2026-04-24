namespace Ksp2Redux.Tools.Uploader;

public class UploadManifest
{
    public string Repository { get; set; }
    public string Branch { get; set; }
    public string File { get; set; }
    public string Token { get; set; }
    public string Version { get; set; }
    public string? Changelog { get; set; }
    public List<PatchManifest> Patches { get; set; }
}