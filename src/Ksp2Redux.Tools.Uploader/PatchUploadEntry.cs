namespace Ksp2Redux.Tools.Uploader;

public sealed record PatchUploadEntry
{
    public required string File { get; init; }

    public string? PreviousVersion { get; init; }
}
