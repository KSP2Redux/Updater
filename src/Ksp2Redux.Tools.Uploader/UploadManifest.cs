namespace Ksp2Redux.Tools.Uploader;

public sealed record UploadManifest
{
    public required string Repository { get; init; }
    public required string Branch { get; init; }
    public required string File { get; init; }
    public required string Token { get; init; }
    public required string Version { get; init; }

    public bool Prerelease { get; init; }

    public string? Label { get; init; }
    public string? Changelog { get; init; }
    public List<PatchUploadEntry>? Patches { get; init; }

    public string? Channel { get; init; }
    public string? R2AccountId { get; init; }
    public string? R2Bucket { get; init; }
    public string? R2AccessKeyId { get; init; }
    public string? R2SecretAccessKey { get; init; }
    public string? R2Endpoint { get; init; }
    public string R2PublicBaseUrl { get; init; } = "https://download.ksp2redux.org";
    public string? CloudflareZoneId { get; init; }
    public string? CloudflareApiToken { get; init; }
    public int RetainBetaReleases { get; init; } = 5;
    public bool MigrateExisting { get; init; }
    public bool StageOnly { get; init; }
    public bool R2ManifestOnly { get; init; }
}
