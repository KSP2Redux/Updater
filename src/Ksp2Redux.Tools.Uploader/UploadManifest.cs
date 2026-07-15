namespace Ksp2Redux.Tools.Uploader;

public sealed record UploadManifest
{
    public required string Repository { get; init; }
    public required string Branch { get; init; }
    public required string File { get; init; }
    public required string Token { get; init; }
    public required string Version { get; init; }

    public string? Label { get; init; }
    public string? Changelog { get; init; }
    public List<PatchUploadEntry>? Patches { get; init; }
}